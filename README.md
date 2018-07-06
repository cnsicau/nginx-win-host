## Windows Service Host for Nginx

### **Compile**
>>> Compile **host** `nginx` from source `nginx.cs`.
>>>```cmd
>>> %SystemRoot%\Microsoft.NET\Framework\v2.0.50727\csc.exe /target:winexe /out:nginx  nginx.cs
>>>```

### **Usage**

#### 1. copy host `nginx` to nginx root directory(contains `nginx.exe`).

#### 2. create `nginx service`.
open windows **`cmd`** prompt and run :
```cmd
# %NGINX_ROOT% is nginx root directory
sc create nginx binpath= "%NGINX_ROOT%\nginx' start= auto
```

#### 3. start nginx host
```cmd
sc start nginx
```

#### 4. stop nginx host
```cmd
sc stop nginx
```

#### 5. send `nginx -s reopen` command
```cmd
sc pause nginx
```

#### 6. send `nginx -s reload` command
```cmd
sc continue nginx
```