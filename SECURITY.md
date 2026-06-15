# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities privately - not in public issues.

Use GitHub's private vulnerability reporting: open the **Security** tab of this
repository and choose **Report a vulnerability**, or go directly to
<https://github.com/JeremyKuhne/filtrace/security/advisories/new>. This opens a
private advisory visible only to the maintainer.

Include, as far as you can:

- a description of the issue and its impact;
- the version or commit affected;
- steps to reproduce, ideally with a minimal trace file or command line;
- any known workaround.

filtrace reads untrusted trace files (`.nettrace`, `.etl`, speedscope JSON), so
parsing and decoding issues - crashes, unbounded allocation, or other
denial-of-service shapes triggered by a malformed capture - are in scope.

## Supported versions

filtrace is pre-1.0. Fixes are applied to the latest release from the `main`
branch; there is no back-port stream yet.

## Response

This is a personal open-source project maintained on a best-effort basis. You can
expect an initial acknowledgement; please allow a reasonable window for a fix
before any public disclosure.
