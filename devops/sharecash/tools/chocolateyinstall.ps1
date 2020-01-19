function WaitUntilServices($searchString, $status)
{
	write-output "Waiting for a service...."
	
    # Get all services where name matches $searchString and loop through each of them.
    foreach($service in (Get-Service -name $searchString))
    {
        # Wait for the service to reach the $status or a maximum of 30 seconds
        $service.WaitForStatus($status, '00:00:30')
    }
}

$packageFolder = $env:ChocolateyPackageFolder

Install-ChocolateyZipPackage -packagename ShareCash -unziplocation c:\ShareCash  -Url $packageFolder\tools\sharecash.zip

# Create & start ShareCash service
new-service -name ShareCash -BinaryPathName "C:\ShareCash\ShareCash.exe"
start-service ShareCash