dotnet publish "%~dp0Picollo.Profiler.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:TrimMode=full
if errorlevel 1 exit /b %errorlevel%
dotnet pack "%~dp0Picollo.Profiler.csproj" -c Release --no-build -r win-x64 -p:PackageVersion=0.0.0 -o "%~dp0..\..\artifacts"
