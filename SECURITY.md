# Security Policy

SqlSchemaHasher is maintained by Zachtbeer Labs B.V.

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly:

1. **Do not open a public issue.**
2. Use [GitHub's private security advisory](https://github.com/zachtbeer-labs/sqlschemahasher/security/advisories/new) to report the vulnerability, or
3. Email [security@zachtbeerlabs.nl](mailto:security@zachtbeerlabs.nl).

## What to Expect

- Acknowledgment within 48 hours.
- A fix or mitigation plan within a reasonable timeframe depending on severity.
- Credit in the release notes (unless you prefer to remain anonymous).

## Supported Versions

Security fixes are applied to the most recent stable major version.

## Scope

This library connects to SQL Server databases using credentials you provide. It reads schema metadata from system catalog views. It does not store, transmit, or log connection strings or credentials.
