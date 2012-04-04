using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Web;
using ServiceStack.Text;
using ServiceStack.Common;
using ServiceStack.Common.Web;
using ServiceStack.WebHost.Endpoints.Extensions;
using ServiceStack.WebHost.Endpoints;
using System.Runtime.Serialization;

namespace ServiceStack.ServiceHost
{
	public static class HttpRequestExtensions
	{
		/// <summary>
		/// Gets string value from Items[name] then Cookies[name] if exists.
		/// Useful when *first* setting the users response cookie in the request filter.
		/// To access the value for this initial request you need to set it in Items[].
		/// </summary>
		/// <returns>string value or null if it doesn't exist</returns>
		public static string GetItemOrCookie(this IHttpRequest httpReq, string name)
		{
			object value;
			if (httpReq.Items.TryGetValue(name, out value)) return value.ToString();

			Cookie cookie;
			if (httpReq.Cookies.TryGetValue(name, out cookie)) return cookie.Value;

			return null;
		}

		/// <summary>
		/// Gets request paramater string value by looking in the following order:
		/// - QueryString[name]
		/// - FormData[name]
		/// - Cookies[name]
		/// - Items[name]
		/// </summary>
		/// <returns>string value or null if it doesn't exist</returns>
		public static string GetParam(this IHttpRequest httpReq, string name)
		{
			string value;
			if ((value = httpReq.Headers[HttpHeaders.XParamOverridePrefix + name]) != null) return value;
			if ((value = httpReq.QueryString[name]) != null) return value;
			if ((value = httpReq.FormData[name]) != null) return value;

			Cookie cookie;
			if (httpReq.Cookies.TryGetValue(name, out cookie)) return cookie.Value;

			object oValue;
			if (httpReq.Items.TryGetValue(name, out oValue)) return oValue.ToString();

			return null;
		}

		public static string GetParentAbsolutePath(this IHttpRequest httpReq)
		{
			return httpReq.GetAbsolutePath().ToParentPath();
		}

		public static string GetAbsolutePath(this IHttpRequest httpReq)
		{
			var resolvedPathInfo = httpReq.PathInfo;

			var pos = httpReq.RawUrl.IndexOf(resolvedPathInfo, StringComparison.InvariantCultureIgnoreCase);
			if (pos == -1)
				throw new ArgumentException(
					String.Format("PathInfo '{0}' is not in Url '{1}'", resolvedPathInfo, httpReq.RawUrl));

			return httpReq.RawUrl.Substring(0, pos + resolvedPathInfo.Length);
		}

		public static string GetParentPathUrl(this IHttpRequest httpReq)
		{
			return httpReq.GetPathUrl().ToParentPath();
		}
		
		public static string GetPathUrl(this IHttpRequest httpReq)
		{
			var resolvedPathInfo = httpReq.PathInfo;

			var pos = resolvedPathInfo == String.Empty
				? httpReq.AbsoluteUri.Length
				: httpReq.AbsoluteUri.IndexOf(resolvedPathInfo, StringComparison.InvariantCultureIgnoreCase);

			if (pos == -1)
				throw new ArgumentException(
					String.Format("PathInfo '{0}' is not in Url '{1}'", resolvedPathInfo, httpReq.RawUrl));

			return httpReq.AbsoluteUri.Substring(0, pos + resolvedPathInfo.Length);
		}

		public static string GetUrlHostName(this IHttpRequest httpReq)
		{
			var aspNetReq = httpReq as HttpRequestWrapper;
			if (aspNetReq != null)
			{
				return aspNetReq.UrlHostName;
			}
			var uri = httpReq.AbsoluteUri;

			var pos = uri.IndexOf("://") + "://".Length;
			var partialUrl = uri.Substring(pos);
			var endPos = partialUrl.IndexOf('/');
			if (endPos == -1) endPos = partialUrl.Length;
			var hostName = partialUrl.Substring(0, endPos).Split(':')[0];
			return hostName;
		}

		public static string GetPhysicalPath(this IHttpRequest httpReq)
		{
			var aspNetReq = httpReq as HttpRequestWrapper;
			if (aspNetReq != null)
			{
				return aspNetReq.Request.PhysicalPath;
			}

			var filePath = httpReq.ApplicationFilePath.CombineWith(httpReq.PathInfo);
			return filePath;
		}

		public static string GetApplicationUrl(this HttpRequest httpReq)
		{
			var appPath = httpReq.ApplicationPath;
			var baseUrl = httpReq.Url.Scheme + "://" + httpReq.Url.Host;
			if (httpReq.Url.Port != 80) baseUrl += ":" + httpReq.Url.Port;
			var appUrl = baseUrl.CombineWith(appPath);
			return appUrl;
		}

		public static string GetHttpMethodOverride(this IHttpRequest httpReq)
		{
			var httpMethod = httpReq.HttpMethod;

			if (httpMethod != HttpMethods.Post)
				return httpMethod;			

			var overrideHttpMethod = 
				httpReq.Headers[HttpHeaders.XHttpMethodOverride].ToNullIfEmpty()
				?? httpReq.FormData[HttpHeaders.XHttpMethodOverride].ToNullIfEmpty()
				?? httpReq.QueryString[HttpHeaders.XHttpMethodOverride].ToNullIfEmpty();

			if (overrideHttpMethod != null)
			{
				if (overrideHttpMethod != HttpMethods.Get && overrideHttpMethod != HttpMethods.Post)
					httpMethod = overrideHttpMethod;
			}

			return httpMethod;
		}

		public static string GetFormatModifier(this IHttpRequest httpReq)
		{
			var format = httpReq.QueryString["format"];
			if (format == null) return null;
			var parts = format.SplitOnFirst('.');
			return parts.Length > 1 ? parts[1] : null;
		}

		public static bool HasNotModifiedSince(this IHttpRequest httpReq, DateTime? dateTime)
		{
			if (!dateTime.HasValue) return false;
			var strHeader = httpReq.Headers[HttpHeaders.IfModifiedSince];
			try
			{
				if (strHeader != null)
				{
					var dateIfModifiedSince = DateTime.ParseExact(strHeader, "r", null);
					var utcFromDate = dateTime.Value.ToUniversalTime();
					//strip ms
					utcFromDate = new DateTime(
						utcFromDate.Ticks - (utcFromDate.Ticks % TimeSpan.TicksPerSecond),
						utcFromDate.Kind
					);

					return utcFromDate <= dateIfModifiedSince;
				}
				return false;
			}
			catch
			{
				return false;
			}
		}

		public static bool DidReturn304NotModified(this IHttpRequest httpReq, DateTime? dateTime, IHttpResponse httpRes)
		{
			if (httpReq.HasNotModifiedSince(dateTime))
			{
				httpRes.StatusCode = (int) HttpStatusCode.NotModified;
				return true;
			}
			return false;
		}

		public static string GetJsonpCallback(this IHttpRequest httpReq)
		{
			return httpReq == null ? null : httpReq.QueryString["callback"];
		}

		public static object GetRequestDto(this IHttpRequest httpReq, Type requestType, string contentType = null, Func<object, object> populateRequestDtoFn = null)
		{
			try
			{
				if (contentType == null)
					contentType = httpReq.ContentType;

				//Use custom request binder if registered
				Func<IHttpRequest, object> requestFactoryFn;
				EndpointHost.ServiceManager.ServiceController.RequestTypeFactoryMap.TryGetValue(requestType, out requestFactoryFn);
				if (requestFactoryFn != null)
					return requestFactoryFn(httpReq);

				object requestDto = null;
				if (!string.IsNullOrEmpty(contentType) && httpReq.ContentLength > 0)
				{
					//Get deserializer for content tpye (eg JSON)
					var deserializer = EndpointHost.AppHost.ContentTypeFilters.GetStreamDeserializer(contentType);
					if (deserializer != null)
						requestDto = deserializer(requestType, httpReq.InputStream);
				}

				//Enable custom deserialization logic for specific endpoints (eg REST) 
				if (populateRequestDtoFn != null)
					return populateRequestDtoFn(requestDto);

				return requestDto;
			}
			catch (Exception ex)
			{
				var msg = "Could not deserialize '{0}' request using {1}'\nError: {2}"
					.Fmt(contentType, requestType, ex);
				throw new SerializationException(msg);
			}
		}


		public static Dictionary<string, string> CookiesAsDictionary(this IHttpRequest httpReq)
		{
			var map = new Dictionary<string, string>();
			var aspNet = httpReq.OriginalRequest as HttpRequest;
			if (aspNet != null)
			{
				foreach (var name in aspNet.Cookies.AllKeys)
				{
					var cookie = aspNet.Cookies[name];
					if (cookie == null) continue;
					map[name] = cookie.Value;
				}
			}
			else
			{
				var httpListener = httpReq.OriginalRequest as HttpListenerRequest;
				if (httpListener != null)
				{
					for (var i = 0; i < httpListener.Cookies.Count; i++)
					{
						var cookie = httpListener.Cookies[i];
						if (cookie == null || cookie.Name == null) continue;
						map[cookie.Name] = cookie.Value;
					}
				}
			}
			return map;
		}

	}
}