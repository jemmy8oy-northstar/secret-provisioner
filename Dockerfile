# syntax=docker/dockerfile:1

# secret-provisioner, as one image: the C# service in `backend/`, nothing else.
# The JavaScript in `src/` is what it was ported from and never runs in here.

# ── build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The whole `backend/` rather than one COPY per project: a project added to the
# solution and missing from a per-project list fails restore ONLY here, after the
# merge — which is how balenthiran.co.uk's image went two days without building.
COPY backend/ ./backend/
RUN dotnet publish backend/Balenthiran.SecretProvisioner.WebApi/Balenthiran.SecretProvisioner.WebApi.csproj -c Release -o /out

# ── serve ────────────────────────────────────────────────────────────────────
# The Debian aspnet image, as Kit uses: it ships ca-certificates, and every call
# this service makes — the Kubernetes API, raw.githubusercontent.com — is HTTPS.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY --from=build /out ./

# Read once at start (`ProvisionerSettings.FromEnvironment`): the pod refuses to
# start without `ConnectionStrings__DefaultConnection`, which the chart injects
# from a Secret. Nothing secret is baked in here.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Balenthiran.SecretProvisioner.WebApi.dll"]
