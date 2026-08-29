# Multi-stage Dockerfile for .NET 8 MCP Server
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy solution and project files
COPY DotNetDevOpsMcpServer.sln ./
COPY src/DotNetDevOpsMcpServer/DotNetDevOpsMcpServer.csproj ./src/DotNetDevOpsMcpServer/
COPY tests/DotNetDevOpsMcpServer.Tests/DotNetDevOpsMcpServer.Tests.csproj ./tests/DotNetDevOpsMcpServer.Tests/

# Restore dependencies
RUN dotnet restore

# Copy full source and build
COPY . .
WORKDIR /app/src/DotNetDevOpsMcpServer
RUN dotnet publish -c Release -o /out

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out ./

# Cloud Run listens on PORT environment variable (default 8080)
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DotNetDevOpsMcpServer.dll", "--transport=sse", "--port=8080"]
