# syntax=docker/dockerfile:1
# Stage 1: compile the React/TypeScript UI into the API's wwwroot.
FROM node:22-alpine AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json* ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN mkdir -p /repo/src/OneArr.Api && npx tsc --noEmit && npx vite build

# Stage 2: restore, test and publish the ASP.NET Core host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY OneArr.slnx ./
COPY src ./src
COPY tests ./tests
COPY --from=web /repo/src/OneArr.Api/wwwroot ./src/OneArr.Api/wwwroot
RUN dotnet publish src/OneArr.Api/OneArr.Api.csproj -c Release -o /out --no-self-contained

# Stage 3: small runtime image, non-root user, single port.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ONEARR_CONFIG_DIR=/config \
    ONEARR_PORT=8780 \
    ONEARR_LISTEN_ADDRESS=0.0.0.0 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
COPY --from=build /out ./
RUN mkdir -p /config /data && chown -R app:app /config
USER app
VOLUME ["/config"]
EXPOSE 8780
ENTRYPOINT ["dotnet", "OneArr.Api.dll"]
