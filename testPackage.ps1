push-location

& "$PSScriptRoot\createShareCashPackage.ps1"

choco install -y virtualbox --version 6.0.14
choco upgrade -y git vagrant

$chocoTestDir = "$PSScriptRoot\..\chocolatey-test-environment"
$repoUrl = "https://github.com/chocolatey-community/chocolatey-test-environment.git"

if ($(test-path $chocoTestDir -pathtype container) -eq $false)
{
	cd $PSScriptRoot\..

	# clone repo
	git clone $repoUrl
 
	cd chocolatey-test-environment
 
	vagrant plugin install sahara

	# bring up test VM
	vagrant up

	# create base line snapshot
	vagrant sandbox on
}

# copy over packages
copy $PSScriptRoot\devops\sharecash\*.nupkg $chocoTestDir\packages\
copy $PSScriptRoot\devops\sharecash-dev-core\*.nupkg $chocoTestDir\packages\
copy $PSScriptRoot\devops\sharecash-dev-vs2019community\*.nupkg $chocoTestDir\packages\

# test run of package install
$packages = @("sharecash", "sharecash-dev-core", "sharecash-dev-vs2019community")
$validExitCodes = @(0, 1605, 1614, 1641, 3010)

foreach ($package in $packages)
{
	write-output "Testing $package"

	cd $chocoTestDir
	
	# remove local changes
	git checkout .	
	
	# configure install of package
	((Get-Content -path $chocoTestDir\Vagrantfile -Raw) -replace '#choco.exe install -fdvy INSERT_NAME  --allow-downgrade', "choco.exe install -fdvy $package --allow-downgrade") | Set-Content -Path $chocoTestDir\Vagrantfile

	vagrant sandbox rollback
	vagrant provision
	
	$exitCode = $LASTEXITCODE

	Write-Host "Exit code was $exitCode"
	if ($validExitCodes -notcontains $exitCode) {
	  Exit 1
	}
}

pop-location