// Package erp reads master data from the ERP: OData pages of changes since a time, following @odata.nextLink, and
// single records by id. It signs in with OAuth 2.0 client credentials (one token, renewed a minute before it expires,
// one renewal at a time) and retries temporary trouble with the retry package.
package erp

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
)

// ErrCredentialsRefused: the ERP does not accept our client (token endpoint 400/401) or our token (401 after renewing).
var ErrCredentialsRefused = errors.New("the ERP refused the credentials")

// maxBody is the most we read of one answer: a page of 1000 orders with 100 lines each fits easily.
const maxBody = 64 << 20

// Config is where the ERP is and who we are. ClientSecret never leaves through String or GoString.
type Config struct {
	BaseURL      string // the ERP's root, e.g. http://mock-erp:8080/ (the sets are under data/)
	TokenURL     string
	ClientID     string
	ClientSecret string
	Scope        string // optional
	PageSize     int    // $top, 1 to 1000
	HTTP         *http.Client
	Retry        retry.Policy
	Now          func() time.Time // nil: the clock
}

func (c Config) String() string {
	return fmt.Sprintf("erp.Config{BaseURL:%s TokenURL:%s ClientID:%s ClientSecret:[hidden] Scope:%s PageSize:%d}", c.BaseURL, c.TokenURL, c.ClientID, c.Scope, c.PageSize)
}

func (c Config) GoString() string { return c.String() }

// Client is safe for use by many goroutines.
type Client struct {
	cfg  Config
	base *url.URL

	mu      sync.Mutex // one renewal at a time; held while a token is fetched
	token   string
	expires time.Time
}

// New checks the configuration; it does not call the ERP yet.
func New(cfg Config) (*Client, error) {
	base, err := url.Parse(cfg.BaseURL)
	if err != nil || !base.IsAbs() {
		return nil, errors.New("erp: BaseURL must be an absolute URL")
	}
	if !strings.HasSuffix(base.Path, "/") {
		base.Path += "/"
	}
	if cfg.PageSize < 1 || cfg.PageSize > 1000 {
		return nil, errors.New("erp: PageSize must be 1 to 1000")
	}
	if cfg.HTTP == nil {
		cfg.HTTP = &http.Client{Timeout: 60 * time.Second}
	}
	if cfg.Now == nil {
		cfg.Now = time.Now
	}
	return &Client{cfg: cfg, base: base}, nil
}

type page struct {
	Value    *[]json.RawMessage `json:"value"`
	NextLink string             `json:"@odata.nextLink"`
}

// Changes reads every record of set changed at or after since (all of them when since is zero), in change order,
// and hands each page to fn. It stops at the first error, fn's included. A nextLink must stay on the ERP's server.
func Changes[T Record](ctx context.Context, c *Client, set Set[T], since time.Time, fn func([]T) error) error {
	q := url.Values{}
	if !since.IsZero() {
		q.Set("$filter", "modifiedDateTime ge "+since.UTC().Format(time.RFC3339Nano))
	}
	q.Set("$orderby", "modifiedDateTime,id")
	q.Set("$top", strconv.Itoa(c.cfg.PageSize))
	next := c.base.JoinPath("data", set.Name)
	next.RawQuery = q.Encode()

	for pages := 1; next != nil; pages++ {
		if pages > 100_000 {
			return errors.New("erp: nextLink never ends")
		}
		body, status, err := c.get(ctx, next)
		if err != nil {
			return fmt.Errorf("erp: reading %s page %d: %w", set.Name, pages, err)
		}
		if status != http.StatusOK {
			return fmt.Errorf("erp: reading %s page %d: answer %d", set.Name, pages, status)
		}
		var p page
		if err := strictInto(body, &p); err != nil || p.Value == nil {
			return fmt.Errorf("erp: %s page %d is not an OData page of the agreed format", set.Name, pages)
		}
		records := make([]T, 0, len(*p.Value))
		for i, raw := range *p.Value {
			var r T
			if err := decodeStrict(raw, &r); err != nil {
				return fmt.Errorf("erp: %s page %d, entry %d is not of the agreed format: %w", set.Name, pages, i+1, err)
			}
			records = append(records, r)
		}
		if err := fn(records); err != nil {
			return err
		}
		next = nil
		if p.NextLink != "" {
			if next, err = c.sameServer(p.NextLink); err != nil {
				return fmt.Errorf("erp: %s page %d: %w", set.Name, pages, err)
			}
		}
	}
	return nil
}

// One reads a single record by its id; found is false when the ERP does not have it (404).
func One[T Record](ctx context.Context, c *Client, set Set[T], id string) (record T, found bool, err error) {
	// OData key literal: quotes doubled, then escaped as one path segment (a slash stays inside the key).
	u := *c.base
	u.Path = c.base.Path + "data/" + set.Name + "('" + strings.ReplaceAll(id, "'", "''") + "')"
	u.RawPath = c.base.EscapedPath() + "data/" + set.Name + "('" + url.PathEscape(strings.ReplaceAll(id, "'", "''")) + "')"
	body, status, err := c.get(ctx, &u)
	if err != nil {
		return record, false, fmt.Errorf("erp: reading %s %s: %w", set.Name, id, err)
	}
	switch status {
	case http.StatusOK:
		if err := decodeStrict(body, &record); err != nil {
			return record, false, fmt.Errorf("erp: %s %s is not of the agreed format: %w", set.Name, id, err)
		}
		return record, true, nil
	case http.StatusNotFound:
		return record, false, nil
	default:
		return record, false, fmt.Errorf("erp: reading %s %s: answer %d", set.Name, id, status)
	}
}

// sameServer parses a nextLink and refuses one that leaves the ERP: our token must not go anywhere else.
func (c *Client) sameServer(link string) (*url.URL, error) {
	u, err := url.Parse(link)
	if err != nil {
		return nil, errors.New("the nextLink is not a URL")
	}
	u = c.base.ResolveReference(u)
	if !strings.EqualFold(u.Scheme, c.base.Scheme) || !strings.EqualFold(u.Host, c.base.Host) {
		return nil, errors.New("the nextLink leads to another server")
	}
	return u, nil
}

// get sends one GET with the bearer token, retrying temporary trouble; a 401 renews the token once and repeats once.
func (c *Client) get(ctx context.Context, u *url.URL) ([]byte, int, error) {
	for attempt := 1; ; attempt++ {
		token, err := c.tokenNow(ctx)
		if err != nil {
			return nil, 0, err
		}
		resp, err := c.cfg.Retry.Send(ctx, c.cfg.HTTP, func(ctx context.Context) (*http.Request, error) {
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
			if err != nil {
				return nil, err
			}
			req.Header.Set("Authorization", "Bearer "+token)
			req.Header.Set("Accept", "application/json")
			return req, nil
		})
		if err != nil {
			return nil, 0, err
		}
		body, err := io.ReadAll(io.LimitReader(resp.Body, maxBody))
		_ = resp.Body.Close()
		if err != nil {
			return nil, 0, err
		}
		if resp.StatusCode == http.StatusUnauthorized {
			if attempt == 1 {
				c.forget(token)
				continue
			}
			return nil, 0, ErrCredentialsRefused
		}
		return body, resp.StatusCode, nil
	}
}

// tokenNow gives the current token, or a new one when there is none (never fetched, or forgotten after a 401) or it has
// less than a minute left. Callers wait for one renewal instead of each fetching their own.
func (c *Client) tokenNow(ctx context.Context) (string, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.token != "" && c.cfg.Now().Before(c.expires.Add(-time.Minute)) {
		return c.token, nil
	}
	token, lifetime, err := c.fetchToken(ctx)
	if err != nil {
		return "", err
	}
	c.token, c.expires = token, c.cfg.Now().Add(lifetime)
	return c.token, nil
}

// forget drops a token the ERP refused, unless another caller has already replaced it.
func (c *Client) forget(token string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.token == token {
		c.token = ""
	}
}

type tokenAnswer struct {
	AccessToken string `json:"access_token"`
	TokenType   string `json:"token_type"`
	ExpiresIn   int    `json:"expires_in"`
}

func (c *Client) fetchToken(ctx context.Context) (string, time.Duration, error) {
	form := url.Values{"grant_type": {"client_credentials"}, "client_id": {c.cfg.ClientID}, "client_secret": {c.cfg.ClientSecret}}
	if c.cfg.Scope != "" {
		form.Set("scope", c.cfg.Scope)
	}
	resp, err := c.cfg.Retry.Send(ctx, c.cfg.HTTP, func(ctx context.Context) (*http.Request, error) {
		req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.cfg.TokenURL, strings.NewReader(form.Encode()))
		if err != nil {
			return nil, err
		}
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		req.Header.Set("Accept", "application/json")
		return req, nil
	})
	if err != nil {
		return "", 0, fmt.Errorf("erp: could not get a token: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusBadRequest || resp.StatusCode == http.StatusUnauthorized {
		return "", 0, ErrCredentialsRefused
	}
	if resp.StatusCode != http.StatusOK {
		return "", 0, fmt.Errorf("erp: could not get a token: answer %d", resp.StatusCode)
	}
	var answer tokenAnswer
	if err := json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&answer); err != nil ||
		answer.AccessToken == "" || !strings.EqualFold(answer.TokenType, "Bearer") || answer.ExpiresIn < 1 {
		return "", 0, errors.New("erp: the token endpoint's answer is not a bearer token")
	}
	return answer.AccessToken, time.Duration(answer.ExpiresIn) * time.Second, nil
}
