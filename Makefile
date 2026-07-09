# Makefile para WindowsLikeSteamOS

# Variables
PROJECT_NAME = WindowsLikeSteamOS
PUBLISH_CMD = dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false

.PHONY: all build publish clean run

# Objetivo por defecto
all: publish

# Construcción estándar (rápida, para desarrollo)
build:
	@echo "Compilando en modo Debug..."
	dotnet build

# Compilación final (Single File Executable)
publish:
	@echo "Compilando ejecutable final autocontenido..."
	$(PUBLISH_CMD)
	@echo "¡Compilación terminada! El ejecutable está en: bin\Release\net8.0-windows\win-x64\publish\$(PROJECT_NAME).exe"

# Ejecutar el proyecto en modo desarrollo
run:
	@echo "Ejecutando proyecto..."
	dotnet run

# Limpiar archivos temporales y builds anteriores
clean:
	@echo "Limpiando directorios bin y obj..."
	dotnet clean
	@if exist bin rmdir /s /q bin
	@if exist obj rmdir /s /q obj
	@echo "Limpieza completada."
