package retry

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// recorder is a Policy whose waits are written down instead of slept.
type recorder struct {
	waits []time.Duration
}

func (r *recorder) policy(jitter float64) Policy {
	p := Default()
	p.Sleep = func(ctx context.Context, d time.Duration) error {
		r.waits = append(r.waits, d)
		return ctx.Err()
	}
	p.Jitter = func() float64 { return jitter }
	p.Now = func() time.Time { return time.Date(2026, 11, 22, 9, 0, 0, 0, time.UTC) }
	return p
}

// answers serves the given statuses in turn (the last one again once they run out), with optional headers.
func answers(t *testing.T, statuses []int, headers map[int]http.Header) (*httptest.Server, *atomic.Int32) {
	t.Helper()
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		n := int(calls.Add(1)) - 1
		status := statuses[min(n, len(statuses)-1)]
		for k, v := range headers[n] {
			w.Header()[k] = v
		}
		w.WriteHeader(status)
		_, _ = io.WriteString(w, `{"ok":true}`)
	}))
	t.Cleanup(srv.Close)
	return srv, &calls
}

func get(url string) func(context.Context) (*http.Request, error) {
	return func(ctx context.Context) (*http.Request, error) {
		return http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	}
}

func send(t *testing.T, p Policy, url string) (*http.Response, error) {
	t.Helper()
	resp, err := p.Send(context.Background(), http.DefaultClient, get(url))
	if resp != nil {
		t.Cleanup(func() { _ = resp.Body.Close() })
	}
	return resp, err
}

func TestA429WaitsAsLongAsRetryAfterSays(t *testing.T) {
	var rec recorder
	srv, calls := answers(t, []int{429, 200}, map[int]http.Header{0: {"Retry-After": {"7"}}})

	resp, err := send(t, rec.policy(0.5), srv.URL)

	if err != nil || resp.StatusCode != 200 {
		t.Fatalf("got %v, %v", resp, err)
	}
	if calls.Load() != 2 || len(rec.waits) != 1 || rec.waits[0] != 7*time.Second {
		t.Fatalf("calls %d, waits %v; want 2 calls and one wait of 7s", calls.Load(), rec.waits)
	}
}

func TestRetryAfterAsAnHTTPDateWaitsUntilThen(t *testing.T) {
	var rec recorder
	p := rec.policy(0.5)
	when := p.Now().Add(20 * time.Second).Format(http.TimeFormat)
	srv, _ := answers(t, []int{503, 200}, map[int]http.Header{0: {"Retry-After": {when}}})

	if _, err := send(t, p, srv.URL); err != nil {
		t.Fatal(err)
	}

	if len(rec.waits) != 1 || rec.waits[0] != 20*time.Second {
		t.Fatalf("waits %v, want [20s]", rec.waits)
	}
}

func TestAPastOrUnreadableRetryAfterFallsBackToTheBackoff(t *testing.T) {
	for _, value := range []string{"Sat, 01 Jan 2000 00:00:00 GMT", "soon", "-5"} {
		var rec recorder
		srv, _ := answers(t, []int{429, 200}, map[int]http.Header{0: {"Retry-After": {value}}})

		if _, err := send(t, rec.policy(0), srv.URL); err != nil {
			t.Fatal(err)
		}

		if len(rec.waits) != 1 || rec.waits[0] != Default().Base/2 {
			t.Fatalf("Retry-After %q: waits %v, want the first backoff %v", value, rec.waits, Default().Base/2)
		}
	}
}

func TestRetryAfterIsCapped(t *testing.T) {
	var rec recorder
	srv, _ := answers(t, []int{429, 200}, map[int]http.Header{0: {"Retry-After": {"99999"}}})

	if _, err := send(t, rec.policy(0.5), srv.URL); err != nil {
		t.Fatal(err)
	}

	if rec.waits[0] != Default().MaxRetryAfter {
		t.Fatalf("waited %v, want the cap %v", rec.waits[0], Default().MaxRetryAfter)
	}
}

func TestA500IsRetriedWithBackoffThatDoublesWithJitter(t *testing.T) {
	// Equal jitter: each wait is between half and all of base * 2^n (capped at Max).
	for _, tc := range []struct {
		jitter float64
		want   []time.Duration
	}{
		{0, []time.Duration{500 * time.Millisecond, time.Second, 2 * time.Second, 4 * time.Second}},
		{1, []time.Duration{time.Second, 2 * time.Second, 4 * time.Second, 8 * time.Second}},
	} {
		var rec recorder
		srv, calls := answers(t, []int{500, 500, 500, 500, 200}, nil)

		resp, err := send(t, rec.policy(tc.jitter), srv.URL)

		if err != nil || resp.StatusCode != 200 || calls.Load() != 5 {
			t.Fatalf("jitter %v: got %v, %v after %d calls", tc.jitter, resp, err, calls.Load())
		}
		if len(rec.waits) != len(tc.want) {
			t.Fatalf("jitter %v: waits %v, want %v", tc.jitter, rec.waits, tc.want)
		}
		for i := range tc.want {
			if rec.waits[i] != tc.want[i] {
				t.Fatalf("jitter %v: waits %v, want %v", tc.jitter, rec.waits, tc.want)
			}
		}
	}
}

func TestBackoffNeverExceedsMax(t *testing.T) {
	var rec recorder
	p := rec.policy(1)
	p.Tries = 10
	srv, _ := answers(t, []int{500}, nil)

	_, _ = send(t, p, srv.URL)

	for _, w := range rec.waits {
		if w > p.Max {
			t.Fatalf("waited %v, more than Max %v", w, p.Max)
		}
	}
}

func TestItGivesUpAfterFiveTries(t *testing.T) {
	var rec recorder
	srv, calls := answers(t, []int{503}, nil)

	resp, err := send(t, rec.policy(0.5), srv.URL)

	var gaveUp *GaveUpError
	if resp != nil || !errors.As(err, &gaveUp) {
		t.Fatalf("got %v, %v; want a GaveUpError", resp, err)
	}
	if calls.Load() != 5 || len(rec.waits) != 4 || gaveUp.Tries != 5 || gaveUp.Status != 503 {
		t.Fatalf("calls %d, waits %d, error %+v", calls.Load(), len(rec.waits), gaveUp)
	}
}

func TestTemporaryAnswers(t *testing.T) {
	for status, want := range map[int]bool{
		200: false, 201: false, 400: false, 401: false, 403: false, 404: false, 409: false, 422: false,
		408: true, 429: true, 500: true, 502: true, 503: true, 504: true,
	} {
		if Temporary(status) != want {
			t.Errorf("Temporary(%d) = %v, want %v", status, !want, want)
		}
	}
}

func TestAPermanentAnswerComesBackAtOnce(t *testing.T) {
	for _, status := range []int{400, 404, 409, 422} {
		var rec recorder
		srv, calls := answers(t, []int{status}, nil)

		resp, err := send(t, rec.policy(0.5), srv.URL)

		if err != nil || resp.StatusCode != status || calls.Load() != 1 || len(rec.waits) != 0 {
			t.Fatalf("%d: got %v, %v after %d calls and %d waits", status, resp, err, calls.Load(), len(rec.waits))
		}
	}
}

func TestAnUnreachableServerIsTemporary(t *testing.T) {
	var rec recorder
	srv := httptest.NewServer(http.NotFoundHandler())
	url := srv.URL
	srv.Close()

	_, err := send(t, rec.policy(0.5), url)

	var gaveUp *GaveUpError
	if !errors.As(err, &gaveUp) || gaveUp.Tries != 5 || gaveUp.Err == nil || len(rec.waits) != 4 {
		t.Fatalf("got %v, waits %v", err, rec.waits)
	}
}

func TestACancelledContextStopsTheWaitAtOnce(t *testing.T) {
	srv, _ := answers(t, []int{429}, map[int]http.Header{0: {"Retry-After": {"3600"}}})
	p := Default() // the real sleep
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	started := time.Now()

	_, err := p.Send(ctx, http.DefaultClient, get(srv.URL))

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("got %v, want the context's error", err)
	}
	if time.Since(started) > 2*time.Second {
		t.Fatalf("took %v: the wait did not stop", time.Since(started))
	}
}

func TestEveryTryGetsAFreshRequest(t *testing.T) {
	var rec recorder
	var bodies []string
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b, _ := io.ReadAll(r.Body)
		bodies = append(bodies, string(b))
		if calls.Add(1) < 3 {
			w.WriteHeader(502)
			return
		}
		w.WriteHeader(204)
	}))
	defer srv.Close()

	resp, err := rec.policy(0.5).Send(context.Background(), http.DefaultClient, func(ctx context.Context) (*http.Request, error) {
		return http.NewRequestWithContext(ctx, http.MethodPut, srv.URL, strings.NewReader(`{"a":1}`))
	})
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()

	if strings.Join(bodies, "|") != `{"a":1}|{"a":1}|{"a":1}` {
		t.Fatalf("bodies %q", bodies)
	}
}
