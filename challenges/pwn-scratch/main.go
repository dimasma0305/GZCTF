// "Stash" — a minimal static-binary service used as an Attack & Defense target.
//
// It serves the rotating flag at GET /. There is no in-image vulnerability to
// speak of; the point of this challenge is its IMAGE PROFILE: a `scratch`
// static binary with no shell. The service works normally, but the admin
// file-inspection and per-round snapshot features cannot exec into it, so they
// degrade to no-ops — a real limitation of the exec-based design that this
// harness is meant to surface.
package main

import (
	"net/http"
	"os"
	"strings"
)

func env(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}

func flag() string {
	b, err := os.ReadFile(env("GZCTF_FLAG_FILE", "/flag"))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(b))
}

func main() {
	http.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("ok"))
	})
	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		f := flag()
		if f == "" {
			w.Write([]byte("no flag yet"))
			return
		}
		w.Write([]byte("flag: " + f))
	})
	http.ListenAndServe(":9000", nil)
}
