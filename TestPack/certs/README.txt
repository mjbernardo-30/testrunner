Put client certificates (.pfx / .p12) for two-way SSL services here.
Reference them in the Environments sheet as certs\your-file.pfx.
Keep the password in a Windows environment variable, e.g. {{$env:SIT1_CERT_PASSWORD}}.
Do not commit real certificates to Git.
