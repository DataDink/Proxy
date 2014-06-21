Proxy
=====

A tiny System.Reflection.Emit based proxy solution

This is an experimental single-class implementation. This is not tested for bugs or performance. Use at your own risk.

### Usage
=========

###### Creating a logging proxy

```C#

    public class ServiceLogger<T> : Proxy<T> where T : class
    {
        public T Proxy { get { return Instance; } }
        public ILogger Logger { get; private set; }

        public ServiceLogger(T service, ILogger logger) : base(service)
        {
            Logger = logger;
        }

        protected override object OnCall(MethodInfo method, object[] parameters)
        {
            try {
                return base.OnCall(method, parameters);
            } catch (Exception ex) {
                Logger.Fatal("{0}.{1} failed : with {2}",
                    method.DeclaringType.Name,
                    method.Name,
                    string.Join(", ", parameters));
                throw;
            }
        }

        protected override void AfterCall(MethodInfo method, object[] parameters, object result)
        {
            Logger.Info("{0}.{1} called : with {2} : returning {3}",
                method.DeclaringType.Name,
                method.Name,
                string.Join(", ", parameters),
                result);
        }
    }
    
```

###### Creating a service proxy

```C#

    public class WebProxy<T> : Proxy<T> where T : class
    {
        public T Service { get { return Instance; } }

        public string ConnectionString { get; private set; }

        public WebProxy(string connection) { ConnectionString = connection; }

        protected override object OnCall(MethodInfo method, object[] parameters)
        {
            using (var client = new WebClient()) { // This is just a cheap example...
                var parms = method.GetParameters()
                    .Select((p, i) => string.Format("{0}={1}", 
                        p.Name, 
                        (parameters[i] ?? "").ToString()));
                var query = string.Join("&", parms);
                var url = string.Format("{0}/{1}?{2}",
                    ConnectionString,
                    method.Name,
                    query);
                var response = client.DownloadString(url);
                return Json.Parse(method.ReturnType, response);
            }
        }
    }
    
```
