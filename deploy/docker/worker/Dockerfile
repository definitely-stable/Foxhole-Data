FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
COPY . .
RUN dotnet restore FoxData.slnx --locked-mode
RUN dotnet publish src/FoxData.Worker/FoxData.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "FoxData.Worker.dll"]
