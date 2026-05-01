# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Acme.sln ./
COPY src/Acme.Domain/*.csproj         src/Acme.Domain/
COPY src/Acme.Application/*.csproj    src/Acme.Application/
COPY src/Acme.Infrastructure/*.csproj src/Acme.Infrastructure/
COPY src/Acme.WebApi/*.csproj         src/Acme.WebApi/
COPY tests/Acme.Tests/*.csproj        tests/Acme.Tests/
RUN dotnet restore Acme.sln

COPY . .
RUN dotnet publish src/Acme.WebApi/Acme.WebApi.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Acme.WebApi.dll"]
