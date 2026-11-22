// Package syncrun is one sync run: vendors, then purchase orders, then goods receipts (so what a record points to is
// there first). For each entity it first reads its open failures again from the ERP by id, then reads the changes since
// the watermark minus an overlap, page by page, and hands every record to the app on a few workers. The watermark is
// saved only after the whole entity went through; a temporary failure that outlasts its retries stops the run and
// leaves the old watermark, so the next run reads the same changes again (the app's upsert makes that harmless).
package syncrun

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"hash/fnv"
	"log/slog"
	"sync"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/app"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/erp"
)

// Runner holds what a run needs. Zero Workers means 1, zero Overlap means none.
type Runner struct {
	ERP            *erp.Client
	App            *app.Client
	Workers        int
	Overlap        time.Duration
	Log            *slog.Logger
	NewCorrelation func() string // nil: 16 random hex digits
}

// Summary is what one entity's part of a run did; it is logged as one line.
type Summary struct {
	Entity      string
	Read        int // records read from the change pages
	Retried     int // open failures read again by id
	Created     int
	Updated     int
	Unchanged   int
	Failed      int // waits (kept for the next run) and refused (skipped)
	Duration    time.Duration
	Correlation string
	Outcome     string // ok, failed or cancelled
}

// Run syncs the three entities in order and stops at the first that fails. It returns the summaries so far.
func (r *Runner) Run(ctx context.Context) ([]Summary, error) {
	correlation := r.correlation()
	steps := []func(context.Context, string) (Summary, error){
		func(ctx context.Context, id string) (Summary, error) {
			return runEntity(ctx, r, id, erp.Vendors, app.Vendors, vendorBody, func(v erp.Vendor) string { return v.ID })
		},
		func(ctx context.Context, id string) (Summary, error) {
			return runEntity(ctx, r, id, erp.PurchaseOrders, app.PurchaseOrders, orderBody, func(o erp.PurchaseOrder) string { return o.ID })
		},
		// Receipts of one order go to one worker, in change order: two of them never race the over-receipt check.
		func(ctx context.Context, id string) (Summary, error) {
			return runEntity(ctx, r, id, erp.ProductReceipts, app.GoodsReceipts, receiptBody, func(p erp.ProductReceipt) string { return p.PurchaseOrderID })
		},
	}
	var summaries []Summary
	for _, step := range steps {
		s, err := step(ctx, correlation)
		summaries = append(summaries, s)
		if err != nil {
			return summaries, err
		}
	}
	return summaries, nil
}

func (r *Runner) correlation() string {
	if r.NewCorrelation != nil {
		return r.NewCorrelation()
	}
	b := make([]byte, 8)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// tally counts results from many workers.
type tally struct {
	mu sync.Mutex
	s  Summary
}

func (t *tally) add(f func(*Summary)) {
	t.mu.Lock()
	defer t.mu.Unlock()
	f(&t.s)
}

func runEntity[T erp.Record](ctx context.Context, r *Runner, correlation string, set erp.Set[T], entity app.Entity,
	body func(T) any, shard func(T) string) (Summary, error) {
	started := time.Now()
	t := &tally{s: Summary{Entity: string(entity), Correlation: correlation}}
	log := r.Log.With("entity", string(entity), "correlation", correlation)

	newest, err := syncEntity(ctx, r, log, t, set, entity, body, shard)
	if err == nil && !newest.IsZero() {
		err = r.App.SetWatermark(ctx, entity, newest)
	}

	s := t.s
	s.Duration = time.Since(started)
	switch {
	case err == nil:
		s.Outcome = "ok"
	case errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded):
		s.Outcome = "cancelled"
	default:
		s.Outcome = "failed"
	}
	attrs := []any{
		"entity", s.Entity, "read", s.Read, "retried", s.Retried, "created", s.Created, "updated", s.Updated,
		"unchanged", s.Unchanged, "failed", s.Failed, "duration", s.Duration.Round(time.Millisecond), "correlation", s.Correlation,
		"outcome", s.Outcome,
	}
	if err != nil {
		attrs = append(attrs, "error", err.Error())
	}
	r.Log.Info("sync summary", attrs...)
	return s, err
}

// syncEntity does the work and returns the newest change time read (zero when nothing was read).
func syncEntity[T erp.Record](ctx context.Context, r *Runner, log *slog.Logger, t *tally, set erp.Set[T], entity app.Entity,
	body func(T) any, shard func(T) string) (time.Time, error) {
	// 1. Open failures first, by id: what waited last time may go through now.
	failures, err := r.App.Failures(ctx, entity)
	if err != nil {
		return time.Time{}, err
	}
	for _, f := range failures {
		record, found, err := erp.One(ctx, r.ERP, set, f.ErpID)
		if err != nil {
			return time.Time{}, err
		}
		t.add(func(s *Summary) { s.Retried++ })
		if !found {
			log.Warn("failed record no longer in the ERP, forgotten", "erp_id", f.ErpID)
			if err := r.App.ClearFailure(ctx, entity, f.ErpID); err != nil {
				return time.Time{}, err
			}
			continue
		}
		if err := handle(ctx, r, log, t, entity, record.Key(), body(record), true); err != nil {
			return time.Time{}, err
		}
	}

	// 2. Changes since the watermark minus the overlap (clocks differ; the overlap is read twice on purpose).
	var since time.Time
	if mark, ok, err := r.App.Watermark(ctx, entity); err != nil {
		return time.Time{}, err
	} else if ok {
		since = mark.Add(-r.Overlap)
	}

	// 3. Workers, each with its own queue; a record's shard key picks the queue, so one key is never in two places.
	workers := max(r.Workers, 1)
	ctx, cancel := context.WithCancelCause(ctx)
	defer cancel(nil)
	queues := make([]chan T, workers)
	var wg sync.WaitGroup
	var firstErr error
	var once sync.Once
	fail := func(err error) {
		once.Do(func() { firstErr = err })
		cancel(err)
	}
	for i := range queues {
		queues[i] = make(chan T, 16)
		wg.Add(1)
		go func(queue <-chan T) {
			defer wg.Done()
			for record := range queue {
				if ctx.Err() != nil {
					continue // drain: the run is stopping
				}
				if err := handle(ctx, r, log, t, entity, record.Key(), body(record), false); err != nil {
					fail(err)
				}
			}
		}(queues[i])
	}

	var newest time.Time
	readErr := erp.Changes(ctx, r.ERP, set, since, func(page []T) error {
		for _, record := range page {
			t.add(func(s *Summary) { s.Read++ })
			if record.Changed().After(newest) {
				newest = record.Changed()
			}
			select {
			case queues[index(shard(record), workers)] <- record:
			case <-ctx.Done():
				return context.Cause(ctx)
			}
		}
		return nil
	})
	for _, q := range queues {
		close(q)
	}
	wg.Wait()

	switch {
	case firstErr != nil:
		return time.Time{}, firstErr
	case readErr != nil:
		return time.Time{}, readErr
	}
	return newest, nil
}

// handle sends one record and counts what came of it. An error stops the run; a refusal does not.
func handle(ctx context.Context, r *Runner, log *slog.Logger, t *tally, entity app.Entity, erpID string, body any, retried bool) error {
	result, err := r.App.Upsert(ctx, entity, erpID, body)
	if err != nil {
		return err
	}
	switch result.Kind {
	case app.Created, app.Updated, app.Unchanged:
		t.add(func(s *Summary) {
			switch result.Kind {
			case app.Created:
				s.Created++
			case app.Updated:
				s.Updated++
			default:
				s.Unchanged++
			}
		})
		if retried {
			return r.App.ClearFailure(ctx, entity, erpID)
		}
	case app.Waits:
		t.add(func(s *Summary) { s.Failed++ })
		log.Info("record waits for another, kept for the next run", "erp_id", erpID, "status", result.Status, "outcome", result.Outcome)
		return r.App.RecordFailure(ctx, entity, erpID, fmt.Sprintf("%d %s: %s", result.Status, result.Outcome, result.Detail))
	case app.Refused:
		t.add(func(s *Summary) { s.Failed++ })
		log.Warn("record refused, skipped", "erp_id", erpID, "status", result.Status, "outcome", result.Outcome, "detail", result.Detail)
		if retried {
			return r.App.ClearFailure(ctx, entity, erpID)
		}
	}
	return nil
}

func index(key string, n int) int {
	h := fnv.New32a()
	_, _ = h.Write([]byte(key))
	return int(h.Sum32() % uint32(n))
}

// ---- Mapping: ERP record → the app's body ----------------------------------------------------------------------------

func vendorBody(v erp.Vendor) any {
	return app.VendorBody{Number: v.AccountNumber, Name: v.Name, VatID: v.VatNumber, Status: v.Status}
}

func orderBody(o erp.PurchaseOrder) any {
	lines := make([]app.OrderLineBody, 0, len(o.Lines))
	for _, l := range o.Lines {
		lines = append(lines, app.OrderLineBody{LineNumber: l.LineNumber, ItemNumber: l.ItemNumber, Description: l.Description,
			Quantity: l.Quantity, UnitPrice: l.UnitPrice, TaxRatePercent: l.TaxRatePercent})
	}
	return app.OrderBody{Number: o.Number, VendorErpID: o.VendorID, OrderDate: o.OrderDate, Currency: o.Currency, Status: o.Status, Lines: lines}
}

func receiptBody(p erp.ProductReceipt) any {
	lines := make([]app.ReceiptLineBody, 0, len(p.Lines))
	for _, l := range p.Lines {
		lines = append(lines, app.ReceiptLineBody{PurchaseOrderLineNumber: l.PurchaseOrderLineNumber, Quantity: l.Quantity})
	}
	return app.ReceiptBody{Number: p.Number, PurchaseOrderErpID: p.PurchaseOrderID, ReceivedDate: p.ReceivedDate, Lines: lines}
}
