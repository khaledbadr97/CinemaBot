# Build the main CinemaBot worker for Linux/Render.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY ["src/CinemaBot.Service/CinemaBot.Service.csproj", "src/CinemaBot.Service/"]
RUN dotnet restore "src/CinemaBot.Service/CinemaBot.Service.csproj"

COPY . .
WORKDIR "/src/src/CinemaBot.Service"
RUN dotnet publish "CinemaBot.Service.csproj" \
    -c Release \
    -o /app/publish \
    --no-self-contained \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

WORKDIR /app
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CinemaBot.Service.dll"]
