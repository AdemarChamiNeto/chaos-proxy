# Imagem do Render (ou qualquer hospedagem com Docker).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Chaos.Domain/Chaos.Domain.csproj src/Chaos.Domain/
COPY src/Chaos.Proxy/Chaos.Proxy.csproj src/Chaos.Proxy/
RUN dotnet restore src/Chaos.Proxy/Chaos.Proxy.csproj
COPY src ./src
RUN dotnet publish src/Chaos.Proxy/Chaos.Proxy.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_ENVIRONMENT=Production PORT=8080
EXPOSE 8080
# usuário sem privilégios que já vem na imagem da Microsoft
USER app
ENTRYPOINT ["dotnet", "Chaos.Proxy.dll"]
