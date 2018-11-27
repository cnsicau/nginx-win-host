## Windows Service Host for Nginx

### **Download**
>#### 1. .NET 3.5  [`nginx.com`](dist/net35/nginx.com)
>#### 2. .NET 4.0+ [`nginx.com`](dist/net40/nginx.com)

### **Usage**

>#### 1. copy `nginx.com` to nginx root directory(contains `nginx.exe`).
>
>#### 2. create `nginx service`.
>open windows **`cmd`** prompt and run :
>```cmd
>nginx --install
>```
>```cmd
Install service nginx.
Success.
>```
>
>#### 3. start `nginx service`.
>```cmd
>net start nginx
>```
>
>#### 4. stop `nginx service`
>```cmd
>net stop nginx
>```
>
>#### 5. manage `nginx` command
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