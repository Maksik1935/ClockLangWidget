Собрал командой dotnet publish ClockLangWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true (поставь dotned и sdk)
Забери .exe из ClockLangWidget\bin\Release\net8.0-windows\win-x64\publish
Автозапуск - Win+R -> shell:startup. Откроется папка, туда просто положи .exe

