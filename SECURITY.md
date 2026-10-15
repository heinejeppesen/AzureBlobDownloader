# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately instead:
the repository's **Security** tab → **Report a vulnerability**.

Please include what you found, the app version and platform, how to reproduce it, and what an attacker
could do with it. You will get a reply as soon as possible, and a fix will be released before the details
are made public.

## Supported versions

Security fixes are made in the latest release only. Update to the newest version before reporting.

## What the app is designed to protect

Useful background when assessing a finding (details in [docs/DESIGN.md](docs/DESIGN.md)):

- The user's sign-in token is only ever sent to Azure Storage blob endpoints
  (`*.blob.core.windows.net`, optionally narrower). Pasted links are validated before any sign-in,
  and the token credential refuses any other audience.
- Sign-in always targets the configured tenant; no device code flow; no Azure Resource Manager or
  Microsoft Graph calls.
- Tokens are cached only in the operating system's secure store (DPAPI, Keychain, keyring), never in plain
  text, and never written to logs.
- Downloaded files never end up outside the chosen folder (unsafe names such as `..` are skipped).
- Dependencies are checked against the GitHub Advisory Database on every build and weekly.
