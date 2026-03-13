# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Bivium/ ./Bivium/
RUN dotnet publish Bivium/Bivium.csproj -c Release -o /app

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV BIVIUM_PORT=5000
ENV BIVIUM_HOME=/data
EXPOSE 5000
ENTRYPOINT ["dotnet", "Bivium.dll"]
