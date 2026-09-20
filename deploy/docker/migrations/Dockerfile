FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .

RUN dotnet restore FoxData.slnx --locked-mode
RUN dotnet tool restore

ENV FOXDATA_DESIGN_CONNECTION="Host=localhost;Port=5432;Database=foxdata_design;Username=foxdata;Password=design"

RUN dotnet ef migrations bundle \
    --project src/FoxData.Infrastructure/FoxData.Infrastructure.csproj \
    --startup-project src/FoxData.Api/FoxData.Api.csproj \
    --context FoxDataDbContext \
    --configuration Release \
    --output /out/foxdata-migrate

FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble AS final
WORKDIR /app
COPY --from=build /out/foxdata-migrate ./foxdata-migrate

USER app
ENTRYPOINT ["/app/foxdata-migrate"]
