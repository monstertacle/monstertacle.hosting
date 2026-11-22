# Implementation plan: Chiseled ASP.NET Core runtime images

1. Confirm the official ASP.NET Core runtime image and SDK are available.
   Prepare the state directory in the SDK stage and copy it with its metadata
   into the common runtime stage.
2. Use engine inspection, copy and export in both smoke modes. Exercise the
   worker's authenticated broker request over the private network and keep the
   existing external HTTPS, metadata and anonymous denial checks.
3. Document host-side diagnosis and validate the exact pull request and main
   revisions on all four hosted CI jobs.

## Constitution check

Build and test with public .NET 11 dependencies and Bash verification.
Keep authentication, durable state, request contracts and deployment credentials
under existing custody. Qualify the containers with temporary certificates and
the deterministic worker. Production deployment and NuGet publication are outside
this change.
