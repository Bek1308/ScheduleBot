# .NET runtime’ni olish
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 8080

# Build qilish
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["ScheduleBot.csproj", "./"]
RUN dotnet restore "ScheduleBot.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "ScheduleBot.csproj" -c Release -o /app/build

# Publish qilish
FROM build AS publish
RUN dotnet publish "ScheduleBot.csproj" -c Release -o /app/publish

# Final image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ScheduleBot.dll"]