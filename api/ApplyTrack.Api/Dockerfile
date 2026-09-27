# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Aaron K. Clark
# Build context is ./api so the solution can grow more projects later.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ApplyTrack.Api/ApplyTrack.Api.csproj ApplyTrack.Api/
RUN dotnet restore ApplyTrack.Api/ApplyTrack.Api.csproj
COPY ApplyTrack.Api/ ApplyTrack.Api/
RUN dotnet publish ApplyTrack.Api/ApplyTrack.Api.csproj -c Release -o /app /p:UseAppHost=false
# The Playwright package ships its driver (node + cli.js) as 0744 root, so the
# agent, run as uid 1654, got EACCES starting it and every packet build and
# browser submission failed (#392). Readable by all, and executable wherever the
# owner could execute (X): the node binary and the directories, not the text.
RUN chmod -R a+rX /app/.playwright

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
# Where a generated encryption key lives when APPLYTRACK_SECRETS_KEY is unset; a
# named volume mounted here inherits this ownership so the app user can write it.
RUN mkdir -p /var/lib/applytrack && chown app:app /var/lib/applytrack
USER app
ENTRYPOINT ["dotnet", "ApplyTrack.Api.dll"]
