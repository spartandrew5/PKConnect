
# PKVault.Core

core-build:
	dotnet build PKVault.Core

core-test:
	dotnet run --project PKVault.Core.Tests

core-test-file:
	dotnet run --project PKVault.Core.Tests -class $(NAME)

# PKConnect.Core

connect-build:
	dotnet build PKConnect.Core

connect-test:
	dotnet run --project PKConnect.Core.Tests

# PKVault.Backend

backend-build:
	dotnet build PKVault.Backend

backend-run:
	dotnet run --project PKVault.Backend

backend-publish:
	dotnet publish PKVault.Backend

# PKVault.Desktop

desktop-build:
	dotnet build PKVault.Desktop

desktop-run:
	dotnet run --project PKVault.Desktop

desktop-publish:
	dotnet publish PKVault.Desktop

# PKVault.Mobile

mobile-build:
	dotnet build PKVault.Mobile

mobile-run:
	dotnet run --project PKVault.Mobile

mobile-publish:
	dotnet publish PKVault.Mobile

# PKVault.Desktop & PKVault.Mobile

apps-prepare:
	npm run --prefix connect-ui build && \
	rm -rf PKVault.Desktop/Resources/wwwroot && \
	cp -r connect-ui/dist PKVault.Desktop/Resources/wwwroot && \
	rm -rf PKVault.Mobile/Resources/Raw/wwwroot && \
	cp -r connect-ui/dist PKVault.Mobile/Resources/Raw/wwwroot

# connect-ui

ui-dev:
	npm run --prefix connect-ui dev

ui-build:
	npm run --prefix connect-ui build

# Scripts

pkhex-update:
	dotnet run --project Scripts update-pkhex

static-data-generate:
	dotnet run --project Scripts gen-static-data

migration-generate:
	dotnet run --project Scripts gen-migration $(NAME)
