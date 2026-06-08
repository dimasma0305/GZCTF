/**
 * Best-effort, module-level belief about whether the visitor currently has an
 * authenticated session.
 *
 * The global 401 interceptor (see App.tsx `authAwareFetcher`) uses this to tell
 * two very different 401s apart:
 *
 *   1. A genuine session expiry — a logged-in user whose cookie lapsed
 *      mid-session. Here we DO want to bounce them to the login page.
 *   2. An anonymous visitor on an otherwise-PUBLIC page (e.g. a game
 *      scoreboard) whose page happens to fire an optional [RequireUser]
 *      enrichment fetch like GET /game/{id}/details. That 401 is expected and
 *      must NOT redirect — the public view should just render.
 *
 * Without this distinction, every public page that fetches team-scoped data
 * forced logged-out users to the login screen.
 *
 * The flag is seeded by `useUser` from the /account/profile probe (the single
 * source of truth for "am I logged in"): set true once a profile loads, false
 * once that probe 401s. It resets to false on every full page load, so a fresh
 * anonymous visit never redirects; an in-session SPA expiry still does, because
 * the earlier successful profile load already set it true.
 */
let authed = false

export const setAuthSession = (value: boolean): void => {
  authed = value
}

export const hasAuthSession = (): boolean => authed

export interface UnauthorizedRedirectContext {
  /** HTTP status of the failed request. */
  status?: number
  /** The request path that failed (first arg to the swagger fetcher). */
  requestPath: string
  /** Current window location pathname. */
  pathname: string
  /** Guard so the redirect fires at most once per navigation. */
  redirectInFlight: boolean
  /** Whether a session is believed to exist; defaults to the live flag. */
  hasSession?: boolean
}

/**
 * Pure decision for the global fetcher: should a failed request bounce the
 * visitor to the login page? Only a real session expiry (we believe a session
 * exists) on a non-auth endpoint of a non-account page qualifies. Anonymous
 * visitors hitting an optional [RequireUser] endpoint on a public page do NOT
 * redirect — that was the scoreboard-forces-login bug.
 */
export const shouldRedirectOnUnauthorized = (ctx: UnauthorizedRedirectContext): boolean => {
  const { status, requestPath, pathname, redirectInFlight } = ctx
  const hasSession = ctx.hasSession ?? authed
  const isAuthEndpoint = requestPath.includes('/account/') || requestPath.includes('/info')
  return (
    status === 401 &&
    hasSession &&
    !redirectInFlight &&
    !isAuthEndpoint &&
    !pathname.startsWith('/account/')
  )
}
