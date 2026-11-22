// Command erp-sync reads master data (vendors, purchase orders, goods receipts) from the ERP and hands it to the
// E-Invoice Inbox's sync API. Settings come from environment variables only (see internal/config). With SYNC_INTERVAL
// unset it runs once and exits 0 on success, 1 on failure; with an interval it runs until stopped (SIGINT/SIGTERM).
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/app"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/config"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/erp"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/syncrun"
)

func main() {
	log := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	cfg, err := config.Load(os.Getenv)
	if err != nil {
		log.Error("cannot start", "error", err.Error())
		os.Exit(2)
	}

	runner, err := newRunner(cfg, log)
	if err != nil {
		log.Error("cannot start", "error", err.Error())
		os.Exit(2)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if cfg.Interval == 0 {
		if err := runOnce(ctx, runner, cfg.RunTimeout); err != nil {
			os.Exit(1)
		}
		return
	}

	log.Info("sync started", "interval", cfg.Interval.String(), "workers", cfg.Workers)
	ticker := time.NewTicker(cfg.Interval)
	defer ticker.Stop()
	for {
		_ = runOnce(ctx, runner, cfg.RunTimeout) // a failed run is logged; the next one tries again
		select {
		case <-ctx.Done():
			log.Info("sync stopped")
			return
		case <-ticker.C:
		}
	}
}

func newRunner(cfg config.Config, log *slog.Logger) (*syncrun.Runner, error) {
	httpClient := &http.Client{Timeout: 60 * time.Second}
	erpClient, err := erp.New(erp.Config{
		BaseURL: cfg.ERPBaseURL, TokenURL: cfg.TokenURL, ClientID: cfg.ClientID, ClientSecret: cfg.ClientSecret, Scope: cfg.Scope,
		PageSize: cfg.PageSize, HTTP: httpClient, Retry: retry.Default(),
	})
	if err != nil {
		return nil, err
	}
	appClient, err := app.New(app.Config{BaseURL: cfg.AppBaseURL, APIKey: cfg.APIKey, HTTP: httpClient, Retry: retry.Default()})
	if err != nil {
		return nil, err
	}
	return &syncrun.Runner{ERP: erpClient, App: appClient, Workers: cfg.Workers, Overlap: 5 * time.Minute, Log: log}, nil
}

func runOnce(ctx context.Context, runner *syncrun.Runner, timeout time.Duration) error {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	_, err := runner.Run(ctx)
	if err != nil && !errors.Is(err, context.Canceled) {
		runner.Log.Error("sync run failed", "error", err.Error())
	}
	return err
}
