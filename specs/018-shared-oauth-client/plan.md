# Implementation plan

Reuse the realm renderer and the private admin HTTPS contract. Add a manifest
client type for shared registrations, a narrowly scoped application script
and a discovery filter in the reference proxy. Exercise realm import,
creation, repeat application, callback change, conflicting settings, token
exchange and rejected anonymous MCP in the published archive fixture.

## Constitution review

The application script uses shell and public dependencies. Credentials only
enter via a private short-lived bearer file. Client registration does not
alter user accounts, broker grants or another client. Review the staged diff
and run the staged secret scanner before each commit.
