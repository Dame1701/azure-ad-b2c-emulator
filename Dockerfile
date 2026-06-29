# --------------------------------------------------------------------------------
#  Local Azure AD B2C emulator. Build context is the repository root.
# --------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AzureAdB2cEmulator/AzureAdB2cEmulator.csproj src/AzureAdB2cEmulator/
RUN dotnet restore src/AzureAdB2cEmulator/AzureAdB2cEmulator.csproj
COPY src/ src/
RUN dotnet publish src/AzureAdB2cEmulator/AzureAdB2cEmulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AzureAdB2cEmulator.dll"]
