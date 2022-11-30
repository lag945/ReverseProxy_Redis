#define enableRedis
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Diagnostics;
using StackExchange.Redis;

namespace rpredis
{
    /// <summary>
    /// rpredis 的摘要描述
    /// </summary>
    public class rpredis : IHttpHandler
    {
        string serverUrl = "http://127.0.0.1:8080";
        string redisUrl = "localhost:6379";
        string pageName = "/rpredis.ashx";
        static ConnectionMultiplexer redis = null;
        bool starACAOl = true;
        int maxAgeDays = 7;
        int redisExpiryDays = 7;

        public void ProcessRequest(HttpContext context)
        {
            // Must be .Net Framework 4.5+
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

            if (!InitRedis())
            {
                context.Response.StatusCode = 500;
                return;
            }

            // Construct the URL from the parameters given to the ashx.
            string url = HttpUtility.UrlDecode(context.Request.Url.ToString());

            int index = url.IndexOf(pageName);

            if (index < 0)
            {
                context.Response.StatusCode = 500;
                return;
            }

            url = url.Substring(index);
            url = url.Replace(pageName, serverUrl);

            if (context.Request.HttpMethod != "GET")
            {
                context.Response.StatusCode = 500;
                return;
            }

            byte[] retBuffer = null;
            var retHeader = new Dictionary<string, string>();
#if enableRedis
            var db = redis.GetDatabase();
            if (db.KeyExists(url))
            {
                var ret = db.HashGetAll(url);
                bool equalDatetime = false;
                for (int i = 0; i < ret.Length; i++)
                {
                    if (ret[i].Name == "raw")
                        retBuffer = ret[i].Value;
                    else if (ret[i].Name == "Last-Modified")
                    {
                        retHeader[ret[i].Name] = ret[i].Value;
                        string date = context.Request.Headers.Get("If-Modified-Since");
                        if (date != null && date == ret[i].Value)
                        {
                            equalDatetime = true;
                        }
                    }
                    else
                        retHeader[ret[i].Name] = ret[i].Value;
                }

                if (equalDatetime)
                {
                    context.Response.StatusCode = 304;
                    return;
                }
            }
#endif
            if (retBuffer == null)
            {

                System.Net.HttpWebRequest req = null;
                try
                {
                    req = System.Net.WebRequest.Create(url) as System.Net.HttpWebRequest;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                    // Throw 404
                    context.Response.StatusCode = 404;
                    return;
                }
                req.UserAgent = context.Request.UserAgent;

                // Copy the necessary headers
                System.Collections.Specialized.NameValueCollection headers = context.Request.Headers;
                for (int i = 0; i < headers.Count; i++)
                {
                    if (headers.GetKey(i) == "Cookie")
                    {
                        req.Headers.Set("Cookie", headers.Get(i));
                    }
                    else if (headers.GetKey(i) == "Authorization")
                    {
                        req.Headers.Set("Authorization", headers.Get(i));
                    }
                    else if (headers.GetKey(i).ToLower().Contains("key"))
                    {
                        req.Headers.Set(headers.GetKey(i), headers.Get(i));
                    }
                    else if (headers.GetKey(i) == "Referer")
                    {
                        req.Referer = headers.Get(i);
                    }
                }

                try
                {
                    System.Net.HttpWebResponse res = req.GetResponse() as System.Net.HttpWebResponse;
                    System.IO.Stream stream_res = res.GetResponseStream();
                    int len = 1024 * 1024;
                    byte[] tmpBuffer = new byte[len]; // 1MB at the time
                    int readlen = 0;
                    System.IO.MemoryStream ms = new System.IO.MemoryStream();
                    do
                    {
                        readlen = stream_res.Read(tmpBuffer, 0, len);
                        ms.Write(tmpBuffer, 0, readlen);
                    }
                    while (readlen > 0);
                    retBuffer = ms.ToArray();

                    // Copy response header
#if enableRedis
                    List<HashEntry> entrys = new List<HashEntry>();
                    entrys.Add(new HashEntry("raw", retBuffer));
#endif
                    for (int i = 0; i < res.Headers.Count; i++)
                    {
                        string[] s = res.Headers.GetValues(i);
                        string k = res.Headers.Keys[i];
                        string v = "";
                        for (int j = 0; j < s.Length; j++)
                        {
                            if (j > 0) v += ";";
                            v += s[j];
                        }
                        retHeader[k] = v;
#if enableRedis
                        entrys.Add(new HashEntry(k, v));
#endif
                    }
#if enableRedis
                    db.HashSet(url, entrys.ToArray());
                    db.KeyExpire(url, TimeSpan.FromDays(redisExpiryDays));
#endif
                }
                catch (System.Net.WebException ex)
                {
                    if (ex.Response != null)
                    {
                        context.Response.StatusCode = (int)(((System.Net.HttpWebResponse)ex.Response).StatusCode);
                        context.Response.StatusDescription = ((System.Net.HttpWebResponse)ex.Response).StatusDescription;
                    }
                    else
                    {
                        // Usually because mapserver is not running.
                        context.Response.StatusCode = 500;
                        context.Response.StatusDescription = "Internal Server error:" + ex.Message;
                    }
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 404;
                    context.Response.StatusDescription = "File not found:" + ex.Message;
                    return;
                }
            }
            SetResponse(context, retHeader, retBuffer);
        }

        public bool IsReusable
        {
            get
            {
                return true;
            }
        }

        private void SetResponse(HttpContext context, Dictionary<string, string> retHeader, byte[] retBuffer)
        {
            if (retHeader != null)
            {
                foreach (string k in retHeader.Keys)
                {
                    if (k == "Content-Type")
                    {
                        context.Response.ContentType = retHeader[k];
                    }
                    else if (k == "Set-Cookie")
                    {
                        context.Response.SetCookie(new HttpCookie(retHeader[k]));
                    }
                    else
                    {
                        context.Response.Headers.Set(k, retHeader[k]);
                    }
                }
            }

            if (context.Request.HttpMethod == "GET")//POST不設max-age
                context.Response.Cache.SetMaxAge(TimeSpan.FromDays(maxAgeDays));
            if (starACAOl)
                context.Response.Headers.Set("Access-Control-Allow-Origin", "*");
            // Write response
            if (retBuffer != null)
                context.Response.BinaryWrite(retBuffer);
        }

        private bool InitRedis()
        {
            try
            {
                if (redis == null)
                {
                    var options = ConfigurationOptions.Parse(redisUrl);
                    options.AbortOnConnectFail = true;
                    redis = ConnectionMultiplexer.Connect(options);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return redis != null;
        }
    }
}