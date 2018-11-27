## Windows Service Host for Nginx

### **Download**
>#### 1 .NET 3.5  [`nginxd`](dist/net35/nginxd.exe) for Windows7, Windows 2008, Windows 2008 r2
>#### 2 .NET 4.0+ [`nginxd`](dist/net40/nginxd.exe) for Windows8, Windows10, Windows 2012+

### **Usage**

>#### 1. copy `nginxd` to nginx root directory(contains `nginx.exe`).
>
>#### 2. create `nginx service`.
>open windows **`cmd`** prompt and run :
>```cmd
>nginxd--install
>```

>```cmd
>Install service nginx.
>Success.
>```
>
>#### 3. remove `nginx service`.
>```cmd
>nginxd --remove
>```

>```cmd
>Remove service nginx.
>Success.
>```
>

>#### 4. start `nginx service`.
>```cmd
>nginxd --start
>```

>```cmd
>Start service nginx.
>Success.
>```
>
>#### 5. stop `nginx service`
>```cmd
>nginxd --stop
>```

>```cmd
>Stop service nginx.
>Success.
>```
>
>#### 6. manage `nginx` command
>```cmd
>nginx -V
>nginx -v
>nginx -t
>nginx -T
>nginx -s reopen
>nginx -s reload
>nginx -s quit
>nginx -s stop
>```