import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import { completeSilentRenewIfIframe } from "./auth";

async function bootstrap() {
  // When Keycloak redirects the hidden silent-renew iframe back to the app origin,
  // finish the token handshake there and skip mounting a second copy of the SPA.
  if (await completeSilentRenewIfIframe()) {
    return;
  }

  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void bootstrap();
