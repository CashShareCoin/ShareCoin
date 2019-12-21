$origDir = $pwd

# Package & install XPlotter
cd $PSScriptRoot\devops\xplotter
choco pack
choco install -y xplotter -s "."

# Package & install Scavenger
cd $PSScriptRoot\devops\scavenger
choco pack
choco install -y scavenger -s "."

cd $origDir