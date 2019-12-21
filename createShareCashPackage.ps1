$origDir = $pwd

& "$PSScriptRoot\createDependencyPackages.ps1"

# Build
cd $PSScriptRoot\ShareCash
dotnet publish -c Release

# Package ShareCash
cd $PSScriptRoot\devops\sharecash
7z a -tzip tools\sharecash.zip $PSScriptRoot\sharecash\bin\release\netcoreapp3.1\publish\*
choco pack

cd $origDir