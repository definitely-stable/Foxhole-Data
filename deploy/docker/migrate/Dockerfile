FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .
RUN dotnet tool restore
RUN dotnet restore FoxData.slnx --locked-mode
RUN FOXDATA_DESIGN_CONNECTION="Host=localhost;Port=5432;Database=foxdata_design;Username=foxdata;Password=design" \
    ASPNETCORE_ENVIRONMENT=Production \
    dotnet ef migrations bundle \
      --project src/FoxData.Infrastructure/FoxData.Infrastructure.csproj \
      --startup-project src/FoxData.Api/FoxData.Api.csproj \
      --context FoxDataDbContext \
      --configuration Release \
      --self-contained \
      --target-runtime linux-x64 \
      --output /out/foxdata-migrate

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-noble AS final
WORKDIR /app
COPY --from=build /out/foxdata-migrate /app/foxdata-migrate
COPY deploy/docker/migrate/entrypoint.sh /app/entrypoint.sh
USER app
ENTRYPOINT ["/app/entrypoint.sh"]
