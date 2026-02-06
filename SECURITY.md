# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly:

1. **Do not open a public issue.**
2. Use [GitHub's private security advisory](https://github.com/zachtbeer/SqlSchemaHasher/security/advisories/new) to report the vulnerability.

## What to Expect

- Acknowledgment within 48 hours.
- A fix or mitigation plan within a reasonable timeframe depending on severity.
- Credit in the release notes (unless you prefer to remain anonymous).

## Scope

This library connects to SQL Server databases using credentials you provide. It reads schema metadata from system catalog views. It does not store, transmit, or log connection strings or credentials.
