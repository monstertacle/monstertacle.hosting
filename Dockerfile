FROM mcr.microsoft.com/dotnet/sdk:11.0 AS source
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY dependencies/communication/src/Monstertacle.Communication.Mcp/ dependencies/communication/src/Monstertacle.Communication.Mcp/
COPY dependencies/communication/LICENSE /licenses/Communication
COPY LICENSE THIRD-PARTY-NOTICES.md /licenses/
RUN install -d -m 0700 -o "$APP_UID" -g 0 /state/hosting

FROM source AS build-broker
RUN dotnet publish src/Monstertacle.Hosting.ControlPlane -c Release -o /publish -p:UseAppHost=false

FROM source AS build-worker
RUN dotnet publish src/Monstertacle.Hosting.WorkerHost -c Release -o /publish -p:UseAppHost=false

FROM source AS build-edge
RUN dotnet publish src/Monstertacle.Hosting.McpHost -c Release -o /publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-extra AS runtime
WORKDIR /app
COPY --from=source /state/ /var/lib/
COPY --from=source /licenses /licenses
USER $APP_UID

FROM runtime AS control-plane
COPY --from=build-broker /publish .
ENTRYPOINT ["dotnet", "Monstertacle.Hosting.ControlPlane.dll"]

FROM runtime AS worker-host
COPY --from=build-worker /publish .
ENTRYPOINT ["dotnet", "Monstertacle.Hosting.WorkerHost.dll"]

FROM source AS build-fixture-issuer
COPY tests/fixtures/ReleaseIssuer/ tests/fixtures/ReleaseIssuer/
RUN dotnet publish tests/fixtures/ReleaseIssuer -c Release -o /publish -p:UseAppHost=false

FROM runtime AS fixture-issuer
COPY --from=build-fixture-issuer /publish .
ENTRYPOINT ["dotnet", "ReleaseIssuer.dll"]

FROM runtime AS mcp-host
COPY --from=build-edge /publish .
ENTRYPOINT ["dotnet", "Monstertacle.Hosting.McpHost.dll"]
