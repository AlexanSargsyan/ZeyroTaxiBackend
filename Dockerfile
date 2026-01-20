# =========================
# Build stage
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["TaxiApi.csproj", "./"]
RUN dotnet restore "TaxiApi.csproj"

# Copy everything else and build
COPY . .
RUN dotnet build "TaxiApi.csproj" -c Release -o /app/build

# Publish the application
FROM build AS publish
RUN dotnet publish "TaxiApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# =========================
# Runtime stage
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install runtime dependencies for OpenCV and Tesseract
RUN apt-get update && apt-get install -y \
    libgdiplus \
    libc6-dev \
    libgl1-mesa-glx \
    libglib2.0-0 \
    tesseract-ocr \
    tesseract-ocr-eng \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:5000

COPY --from=publish /app/publish .

EXPOSE 5000

ENTRYPOINT ["dotnet", "TaxiApi.dll"]
