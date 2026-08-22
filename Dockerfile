# The API as a container. Host-agnostic on purpose: everything that differs
# between Azure, Railway, Fly and a VPS arrives as an environment variable, so
# the same image runs on whichever one gets chosen.
#
#   docker build -t autopartshub-api .
#   docker run --rm -p 8080:8080 \
#     -e DATABASE_URL="postgresql://..." \
#     -e AUTH_SECRET="$(openssl rand -hex 32)" \
#     autopartshub-api
#
# The build stage restores against the project file alone first, so a change
# to the source does not re-download the package graph.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AutoPartsHub.Api/AutoPartsHub.Api.csproj AutoPartsHub.Api/
RUN dotnet restore AutoPartsHub.Api/AutoPartsHub.Api.csproj

COPY AutoPartsHub.Api/ AutoPartsHub.Api/
RUN dotnet publish AutoPartsHub.Api/AutoPartsHub.Api.csproj \
    -c Release -o /app --no-restore

# -extra rather than the bare chiseled image, and this is not a size
# preference. The bare one ships without ICU, which turns on
# globalization-invariant mode, which silently reorders the catalogue — see
# the guard at the top of Program.cs, which refuses to start rather than serve
# a shuffled search. -extra carries ICU and tzdata.
#
# It also runs as a non-root user already and has no shell, which is why there
# is no HEALTHCHECK line here: every host worth deploying to probes /health
# over HTTP itself, and adding a shell back in to curl our own port would cost
# more than it buys.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .

# Production is what makes the difference: no .env is read, the session cookie
# is marked Secure, and a missing or placeholder AUTH_SECRET stops the process
# at startup instead of signing cookies anyone could forge.
#
# PORT, not ASPNETCORE_HTTP_PORTS. Railway, Render and Cloud Run assign a port
# and announce it in PORT; a default baked in under a name they do not set
# would outrank it, and the container would listen politely where nobody is
# looking. Setting PORT here means the image has a sensible default AND those
# hosts override it just by doing what they already do.
ENV ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080 \
    DOTNET_gcServer=1

EXPOSE 8080

ENTRYPOINT ["dotnet", "AutoPartsHub.Api.dll"]
