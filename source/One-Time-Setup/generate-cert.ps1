param (
    [string]$Password = "percolator-dev-cert",
    [string]$DnsName = "localhost"
)

# Define paths relative to script location
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Get-Item $scriptDir).Parent.Parent.FullName
$certOutputDir = Join-Path -Path $projectRoot -ChildPath "source\Percolator.Node\certificates"
$certFile = Join-Path -Path $certOutputDir -ChildPath "percolator-shared-cert.pfx"
$publicCertFile = Join-Path -Path $certOutputDir -ChildPath "percolator-shared-cert.cer"

# Create certificate output directory if it doesn't exist
if (!(Test-Path $certOutputDir)) {
    Write-Host "Creating certificate directory at $certOutputDir..."
    New-Item -ItemType Directory -Path $certOutputDir -Force | Out-Null
}

Write-Host "Generating self-signed certificate for $DnsName..."
$cert = New-SelfSignedCertificate -DnsName $DnsName -CertStoreLocation "cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(10)

# Export the certificate to PFX with private key for the server
$pfxPassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
Write-Host "Exporting certificate with private key to $certFile..."
Export-PfxCertificate -Cert $cert -FilePath $certFile -Password $pfxPassword | Out-Null

# Export public certificate for clients
Write-Host "Exporting public certificate to $publicCertFile..."
Export-Certificate -Cert $cert -FilePath $publicCertFile | Out-Null

# Display certificate information
Write-Host "Certificate generated successfully!"
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "Subject: $($cert.Subject)"
Write-Host "Valid until: $($cert.NotAfter)"
Write-Host "Private key file: $certFile (use this for the server)"
Write-Host "Public key file: $publicCertFile (distribute this to clients)"

# Clean up the certificate from the store
Remove-Item -Path "cert:\CurrentUser\My\$($cert.Thumbprint)" | Out-Null

Write-Host "`nNote: These certificates are automatically loaded by SharedCertificateManager"
Write-Host "      and copied to the build output directory by the Percolator.Node.csproj file."
