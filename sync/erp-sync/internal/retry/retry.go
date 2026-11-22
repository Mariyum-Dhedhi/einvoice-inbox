// Package retry sends an HTTP request again while the answer is temporary: a network error or timeout, 408, 429 or
// 5xx. It waits as long as Retry-After says (capped), else a backoff that doubles with jitter, and gives up after a
// number of tries. A permanent answer (any other status) comes back at once for the caller to read.
package retry

import (
	"context"
	"errors"
	"fmt"
	"io"
	"math/rand/v2"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Policy says how often and how long to wait. The zero value is not useful; start from Default.
type Policy struct {
	Tries         int           // tries in all, the first one included
	Base          time.Duration // the backoff before the second try; doubles after each
	Max           time.Duration // no backoff is longer
	MaxRetryAfter time.Duration // no Retry-After is believed beyond this

	Sleep  func(ctx context.Context, d time.Duration) error // waits d or until ctx ends; nil: a real timer
	Jitter func() float64                                   // a number in [0, 1]; nil: random
	Now    func() time.Time                                 // for Retry-After dates; nil: the clock
}

// Default is five tries, 1 s doubling to at most 30 s, and Retry-After believed up to 2 minutes (a run has a few).
func Default() Policy {
	return Policy{Tries: 5, Base: time.Second, Max: 30 * time.Second, MaxRetryAfter: 2 * time.Minute}
}

// GaveUpError is the last temporary failure after every try was used.
type GaveUpError struct {
	Tries  int
	Status int   // the last answer's status, 0 when there was none
	Err    error // the last network error, nil when there was an answer
}

func (e *GaveUpError) Error() string {
	if e.Err != nil {
		return fmt.Sprintf("gave up after %d tries: %v", e.Tries, e.Err)
	}
	return fmt.Sprintf("gave up after %d tries: the last answer was %d", e.Tries, e.Status)
}

func (e *GaveUpError) Unwrap() error { return e.Err }

// Temporary tells whether an answer with this status may go away by itself.
func Temporary(status int) bool {
	return status == http.StatusRequestTimeout || status == http.StatusTooManyRequests || status >= 500
}

// Send builds and sends a request until the answer is permanent or the tries are used up. Each try gets a request
// of its own from build, so a body can be sent again. The caller closes the body of the response it gets.
func (p Policy) Send(ctx context.Context, client *http.Client, build func(ctx context.Context) (*http.Request, error)) (*http.Response, error) {
	tries := max(p.Tries, 1)
	var last GaveUpError
	for try := 1; ; try++ {
		req, err := build(ctx)
		if err != nil {
			return nil, err
		}
		resp, err := client.Do(req)
		var wait time.Duration
		switch {
		case err != nil:
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			last = GaveUpError{Tries: try, Err: err}
			wait = p.backoff(try)
		case Temporary(resp.StatusCode):
			last = GaveUpError{Tries: try, Status: resp.StatusCode}
			after, ok := p.retryAfter(resp.Header.Get("Retry-After"))
			drain(resp)
			wait = after
			if !ok {
				wait = p.backoff(try)
			}
		default:
			return resp, nil
		}
		if try >= tries {
			return nil, &last
		}
		if err := p.sleep(ctx, wait); err != nil {
			return nil, err
		}
	}
}

// backoff before try n+1: between half and all of Base * 2^(n-1), never more than Max ("equal jitter": never zero,
// and callers that failed together do not come back together).
func (p Policy) backoff(n int) time.Duration {
	d := p.Base
	for i := 1; i < n && d < p.Max; i++ {
		d *= 2
	}
	d = min(d, p.Max)
	jitter := rand.Float64
	if p.Jitter != nil {
		jitter = p.Jitter
	}
	return d/2 + time.Duration(jitter()*float64(d/2))
}

// retryAfter reads seconds or an HTTP date; a past date, a negative number or anything else is not a wait.
func (p Policy) retryAfter(value string) (time.Duration, bool) {
	value = strings.TrimSpace(value)
	if value == "" {
		return 0, false
	}
	var d time.Duration
	if seconds, err := strconv.Atoi(value); err == nil {
		if seconds < 0 {
			return 0, false
		}
		d = time.Duration(seconds) * time.Second
	} else if when, err := http.ParseTime(value); err == nil {
		now := time.Now
		if p.Now != nil {
			now = p.Now
		}
		d = when.Sub(now())
		if d <= 0 {
			return 0, false
		}
	} else {
		return 0, false
	}
	return min(d, p.MaxRetryAfter), true
}

func (p Policy) sleep(ctx context.Context, d time.Duration) error {
	if p.Sleep != nil {
		return p.Sleep(ctx, d)
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

// drain reads a little of a body we will not use, so the connection can be reused, and closes it.
func drain(resp *http.Response) {
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 64<<10))
	_ = resp.Body.Close()
}

// IsGaveUp tells whether err is the end of a temporary failure (the run may try again later).
func IsGaveUp(err error) bool {
	var g *GaveUpError
	return errors.As(err, &g)
}
