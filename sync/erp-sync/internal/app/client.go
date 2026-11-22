// Package app talks to the E-Invoice Inbox's sync API (contracts/sync/README.md): upserts by ERP id, the sync's
// watermarks and its open failures. It signs in with "Authorization: ApiKey <key>" and retries temporary trouble.
package app

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
)

// ErrNotAllowed: the app does not accept our key (401) or the key may not do this (403). Nothing will go through.
var ErrNotAllowed = errors.New("the app refused the sync's API key")

// Entity is one of the app's sync entities, as it appears in the API's paths.
type Entity string

const (
	Vendors        Entity = "vendors"
	PurchaseOrders Entity = "purchase-orders"
	GoodsReceipts  Entity = "goods-receipts"
)

// MaxReasonLength is the app's limit for a failure's reason.
const MaxReasonLength = 200

// Kind is what an upsert came to.
type Kind int

const (
	Created   Kind = iota + 1 // 201
	Updated                   // 200, the record changed
	Unchanged                 // 200, the same values again
	Waits                     // 409: a record it depends on is not there yet; try again next run
	Refused                   // 422 (the rules) or 400 (not the contract): skip it
)

func (k Kind) String() string {
	return map[Kind]string{Created: "created", Updated: "updated", Unchanged: "unchanged", Waits: "waits", Refused: "refused"}[k]
}

// Result is the app's answer to one upsert. Detail is the app's safe problem detail (never the record).
type Result struct {
	Kind    Kind
	Status  int
	Outcome string
	Detail  string
}

// Failure is an open failure the app keeps for the sync.
type Failure struct {
	ErpID     string    `json:"erpId"`
	Reason    string    `json:"reason"`
	FirstSeen time.Time `json:"firstSeen"`
	LastSeen  time.Time `json:"lastSeen"`
	Tries     int       `json:"tries"`
}

// Config is where the app is and the sync's key. APIKey never leaves through String or GoString.
type Config struct {
	BaseURL string
	APIKey  string
	HTTP    *http.Client
	Retry   retry.Policy
}

func (c Config) String() string {
	return fmt.Sprintf("app.Config{BaseURL:%s APIKey:[hidden]}", c.BaseURL)
}

func (c Config) GoString() string { return c.String() }

// Client is safe for use by many goroutines.
type Client struct {
	cfg  Config
	base *url.URL
}

func New(cfg Config) (*Client, error) {
	base, err := url.Parse(cfg.BaseURL)
	if err != nil || !base.IsAbs() {
		return nil, errors.New("app: BaseURL must be an absolute URL")
	}
	if !strings.HasSuffix(base.Path, "/") {
		base.Path += "/"
	}
	if cfg.HTTP == nil {
		cfg.HTTP = &http.Client{Timeout: 60 * time.Second}
	}
	return &Client{cfg: cfg, base: base}, nil
}

// Upsert sends one record. An error means the run cannot go on (gave up, not allowed, an answer we do not know);
// a refusal of the record itself is a Result.
func (c *Client) Upsert(ctx context.Context, entity Entity, erpID string, body any) (Result, error) {
	payload, err := json.Marshal(body)
	if err != nil {
		return Result{}, fmt.Errorf("app: the %s record %s cannot be written as JSON: %w", entity, erpID, err)
	}
	status, answer, err := c.send(ctx, http.MethodPut, c.path("api/sync", string(entity), erpID), payload)
	if err != nil {
		return Result{}, err
	}
	var reply struct {
		Outcome string `json:"outcome"`
		Detail  string `json:"detail"`
	}
	_ = json.Unmarshal(answer, &reply)
	result := Result{Status: status, Outcome: reply.Outcome, Detail: reply.Detail}
	switch {
	case status == http.StatusCreated && reply.Outcome == "Created":
		result.Kind = Created
	case status == http.StatusOK && reply.Outcome == "Updated":
		result.Kind = Updated
	case status == http.StatusOK && reply.Outcome == "Unchanged":
		result.Kind = Unchanged
	case status == http.StatusConflict:
		result.Kind = Waits
	case status == http.StatusUnprocessableEntity || status == http.StatusBadRequest:
		result.Kind = Refused
	default:
		return Result{}, fmt.Errorf("app: unexpected answer %d %q to the %s record %s", status, reply.Outcome, entity, erpID)
	}
	if result.Detail == "" && result.Kind >= Waits {
		result.Detail = http.StatusText(status)
	}
	return result, nil
}

// Watermark is the latest change time a complete run has read; ok is false before the first.
func (c *Client) Watermark(ctx context.Context, entity Entity) (value time.Time, ok bool, err error) {
	status, answer, err := c.send(ctx, http.MethodGet, c.path("api/sync/watermarks", string(entity)), nil)
	if err != nil {
		return time.Time{}, false, err
	}
	var reply struct {
		Value *time.Time `json:"value"`
	}
	if status != http.StatusOK || json.Unmarshal(answer, &reply) != nil {
		return time.Time{}, false, fmt.Errorf("app: reading the %s watermark: answer %d", entity, status)
	}
	if reply.Value == nil {
		return time.Time{}, false, nil
	}
	return reply.Value.UTC(), true, nil
}

func (c *Client) SetWatermark(ctx context.Context, entity Entity, value time.Time) error {
	payload, _ := json.Marshal(map[string]string{"value": value.UTC().Format(time.RFC3339Nano)})
	return c.expect(ctx, http.MethodPut, c.path("api/sync/watermarks", string(entity)), payload, "saving the "+string(entity)+" watermark")
}

// Failures are the open failures of an entity, oldest first.
func (c *Client) Failures(ctx context.Context, entity Entity) ([]Failure, error) {
	status, answer, err := c.send(ctx, http.MethodGet, c.path("api/sync/failures", string(entity)), nil)
	if err != nil {
		return nil, err
	}
	var failures []Failure
	if status != http.StatusOK || json.Unmarshal(answer, &failures) != nil {
		return nil, fmt.Errorf("app: reading the %s failures: answer %d", entity, status)
	}
	return failures, nil
}

// RecordFailure keeps a failure for the next run (or counts another try). The reason is made to fit the app's rule:
// one line of at most 200 characters.
func (c *Client) RecordFailure(ctx context.Context, entity Entity, erpID, reason string) error {
	payload, _ := json.Marshal(map[string]string{"reason": fitReason(reason)})
	return c.expect(ctx, http.MethodPut, c.path("api/sync/failures", string(entity), erpID), payload, "recording a "+string(entity)+" failure")
}

func (c *Client) ClearFailure(ctx context.Context, entity Entity, erpID string) error {
	return c.expect(ctx, http.MethodDelete, c.path("api/sync/failures", string(entity), erpID), nil, "clearing a "+string(entity)+" failure")
}

func fitReason(reason string) string {
	reason = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return ' '
		}
		return r
	}, reason)
	reason = strings.TrimSpace(reason)
	if runes := []rune(reason); len(runes) > MaxReasonLength {
		reason = string(runes[:MaxReasonLength])
	}
	if reason == "" {
		reason = "unknown"
	}
	return reason
}

// path joins fixed parts and escapes each id as one segment.
func (c *Client) path(prefix string, segments ...string) *url.URL {
	u := *c.base
	raw := c.base.EscapedPath() + prefix
	plain := c.base.Path + prefix
	for _, s := range segments {
		raw += "/" + url.PathEscape(s)
		plain += "/" + s
	}
	u.Path, u.RawPath = plain, raw
	return &u
}

func (c *Client) expect(ctx context.Context, method string, u *url.URL, payload []byte, what string) error {
	status, _, err := c.send(ctx, method, u, payload)
	if err != nil {
		return err
	}
	if status != http.StatusNoContent && status != http.StatusOK {
		return fmt.Errorf("app: %s: answer %d", what, status)
	}
	return nil
}

func (c *Client) send(ctx context.Context, method string, u *url.URL, payload []byte) (int, []byte, error) {
	resp, err := c.cfg.Retry.Send(ctx, c.cfg.HTTP, func(ctx context.Context) (*http.Request, error) {
		var body io.Reader
		if payload != nil {
			body = bytes.NewReader(payload)
		}
		req, err := http.NewRequestWithContext(ctx, method, u.String(), body)
		if err != nil {
			return nil, err
		}
		req.Header.Set("Authorization", "ApiKey "+c.cfg.APIKey)
		req.Header.Set("Accept", "application/json")
		if payload != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		return req, nil
	})
	if err != nil {
		return 0, nil, fmt.Errorf("app: %s %s: %w", method, u.Path, err)
	}
	defer resp.Body.Close()
	answer, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return 0, nil, err
	}
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return 0, nil, fmt.Errorf("app: %s %s: answer %d: %w", method, u.Path, resp.StatusCode, ErrNotAllowed)
	}
	return resp.StatusCode, answer, nil
}
