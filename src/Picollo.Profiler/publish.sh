dotnet publish "$(dirname "$0")/Picollo.Profiler.csproj" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:TrimMode=full
