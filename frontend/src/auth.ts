import {
  InMemoryWebStorage,
  User,
  UserManager,
  WebStorageStateStore,
} from "oidc-client-ts";

// ── OIDC configuration ──────────────────────────────────────────────────────

/**
 * Resolves a required OIDC setting. In production the `VITE_*` build-time
 * variable must be supplied — we fail fast instead of silently pointing at a
 * local Keycloak that only exists on a developer machine. The localhost default
 * is a developer convenience (dev server / test runs) only.
 */
function requiredOidcEnv(
  value: string | undefined,
  name: string,
  devDefault: string,
): string {
  if (value) return value;
  if (import.meta.env.DEV) return devDefault;
  throw new Error(
    `${name} is not configured. Set it at build time for production deployments.`,
  );
}

const authority = requiredOidcEnv(
  import.meta.env.VITE_OIDC_AUTHORITY,
  "VITE_OIDC_AUTHORITY",
  "http://localhost:8081/realms/authorization-local",
);
const clientId = requiredOidcEnv(
  import.meta.env.VITE_OIDC_CLIENT_ID,
  "VITE_OIDC_CLIENT_ID",
  "authorization-portal",
);

const redirectUri = `${window.location.origin}/`;

const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: redirectUri,
  post_logout_redirect_uri: window.location.origin,
  // Reuse the app origin as the hidden silent-renew target; completeSilentRenewIfIframe()
  // finishes the handshake when the SPA boots inside the renew iframe.
  silent_redirect_uri: redirectUri,
  response_type: "code",
  scope: "openid",
  // Access/refresh tokens live only in memory: a successful XSS cannot read a persisted
  // admin session from localStorage, and tokens are discarded on reload/tab-close. The
  // transient PKCE verifier must survive the full-page redirect to Keycloak, so the auth
  // state (not the tokens) is parked in per-tab sessionStorage. oidc-client-ts uses PKCE
  // (S256) by default. Sessions are re-established after reload via trySilentSignin().
  userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
  automaticSilentRenew: true,
});

// ── Public helpers ───────────────────────────────────────────────────────────

export async function getUser(): Promise<User | null> {
  return userManager.getUser();
}

export async function login(): Promise<void> {
  try {
    await userManager.signinRedirect();
  } catch (error) {
    console.error("OIDC signinRedirect failed", error);
    throw error;
  }
}

export async function logout(): Promise<void> {
  await userManager.signoutRedirect();
}

/**
 * Attempts to restore a session without any user interaction by asking Keycloak
 * for a token with `prompt=none`, relying on the existing SSO cookie. Because
 * tokens are held only in memory (see the store config above), this runs on every
 * load to re-establish a session after a reload. Returns null when there is no
 * active SSO session, so the caller can fall back to the interactive sign-in.
 */
export async function trySilentSignin(): Promise<User | null> {
  try {
    return await userManager.signinSilent();
  } catch {
    return null;
  }
}

/**
 * When the SPA is loaded inside the hidden silent-renew iframe, completes the
 * renew handshake and returns true so the bootstrap can skip rendering the app.
 * Returns false for a normal top-level page load.
 */
export async function completeSilentRenewIfIframe(): Promise<boolean> {
  const inIframe = window.self !== window.top;
  const params = new URLSearchParams(window.location.search);
  // A silent-renew callback carries either a `code` (session present) or an `error`
  // such as login_required (no active SSO session). Both must be handed to
  // signinSilentCallback so the pending signinSilent() promise resolves/rejects.
  const hasResponse = params.has("code") || params.has("error");
  if (!inIframe || !hasResponse) {
    return false;
  }
  try {
    await userManager.signinSilentCallback();
  } catch (error) {
    console.error("OIDC signinSilentCallback failed", error);
  }
  return true;
}

/**
 * Completes an in-flight authorization-code redirect when the current URL carries
 * an OIDC response (`?code=...`). Returns the signed-in user, or null when there is
 * no callback to process. Always strips the OIDC params from the address bar.
 */
export async function handleRedirectCallback(): Promise<User | null> {
  const params = new URLSearchParams(window.location.search);
  if (!params.has("code") && !params.has("error")) {
    return null;
  }

  try {
    const user = await userManager.signinRedirectCallback();
    return user;
  } catch (error) {
    console.error("OIDC signinRedirectCallback failed", error);
    throw error;
  } finally {
    window.history.replaceState({}, document.title, window.location.pathname);
  }
}

/** Returns a valid access token, silently renewing if needed, or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  let user = await userManager.getUser();
  if (user && user.expired) {
    try {
      user = await userManager.signinSilent();
    } catch {
      user = null;
    }
  }
  return user?.access_token ?? null;
}

export function getDisplayName(user: User | null): string {
  if (!user) {
    return "Signed out";
  }
  const profile = user.profile;
  return (
    (profile.email as string | undefined) ??
    (profile.preferred_username as string | undefined) ??
    (profile.name as string | undefined) ??
    "Authenticated user"
  );
}
