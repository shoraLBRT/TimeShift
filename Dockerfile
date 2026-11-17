# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только файлы проектов — чтобы restore кешировался отдельно от исходников.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/TimeShift.Domain/TimeShift.Domain.csproj src/TimeShift.Domain/
COPY src/TimeShift.Application/TimeShift.Application.csproj src/TimeShift.Application/
COPY src/TimeShift.Infrastructure/TimeShift.Infrastructure.csproj src/TimeShift.Infrastructure/
COPY src/TimeShift.Rendering/TimeShift.Rendering.csproj src/TimeShift.Rendering/
COPY src/TimeShift.Speech/TimeShift.Speech.csproj src/TimeShift.Speech/
COPY src/TimeShift.Telegram/TimeShift.Telegram.csproj src/TimeShift.Telegram/
COPY src/TimeShift.Web/TimeShift.Web.csproj src/TimeShift.Web/
COPY src/TimeShift.Host/TimeShift.Host.csproj src/TimeShift.Host/
RUN dotnet restore src/TimeShift.Host/TimeShift.Host.csproj

COPY src/ src/
RUN dotnet publish src/TimeShift.Host/TimeShift.Host.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Шрифт с кириллицей для картинок /day и /week.
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TimeShift.Host.dll"]
