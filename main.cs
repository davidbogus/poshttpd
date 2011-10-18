using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Threading;

namespace DotHttpd {
	public class Program {
		public static void Main(string[] args) {
			Console.WriteLine("dotHttpd");
			BG main = new BG();
			main.Start();
			
		}
	}
	
	public class HLState {
		
	}
	
	public class BG {
		public string prefix = "http://+:81/";
		HttpListener _listener = new HttpListener();
		Thread t;
		int asyncLimit = 5;
		List<IAsyncResult> async = new List<IAsyncResult>();
		
		public Dictionary<string, IEngine> plugins = new Dictionary<string,IEngine>();
		
		public BG() {
			this._listener.Prefixes.Add(this.prefix);
			this.t = new Thread(new ThreadStart(this.Looper));
			
			this.plugins.Add(".ps1", new PS());
			this.plugins.Add(".psweb", new PS());
			this.plugins.Add("*", new PassThru());
		}
		
		public void Looper() {
			while(t.IsAlive) {
				//if(async.Count(i => i.IsCompleted) <= this.asyncLimit) 
				while(async.Where(i => !i.IsCompleted).ToList().Count >= this.asyncLimit) {
					//Console.Write(".");
					Thread.Sleep(1);
				}
				#region clear out async list
				List<IAsyncResult> tmp = new List<IAsyncResult>();
				foreach(IAsyncResult a in async) {
					if(!a.IsCompleted) {
						tmp.Add(a);
					}
				}
				async.AddRange(tmp);
				#endregion
				
				
				HLState state = new HLState();
				
				
				//_listener.GetContext()
				try {
					async.Add(_listener.BeginGetContext(new AsyncCallback(this.cb), state));
					/*Console.WriteLine("Waiting on incoming...");
					IAsyncResult r = _listener.BeginGetContext(new AsyncCallback(this.cb), state);
					while(!r.IsCompleted) {
						Console.Write(".");
						Thread.Sleep(10);
					}*/
				} catch(Exception e) {
					Console.WriteLine(e);
				}
			}
		}
		protected void cb(HttpListenerContext con) {
			Console.WriteLine("Request Trace ID: {0}", con.Request.RequestTraceIdentifier);
			PassThru pt = new PassThru();
			System.IO.Stream s = (System.IO.Stream)new System.IO.MemoryStream(0);
			
			System.IO.FileInfo f = Path.getABSPath(con.Request.RawUrl);
			
			try {
				Console.WriteLine("Attempting to use engine {0}|{1}...", f.Extension, this.plugins[f.Extension].GetType().Name);
				s = this.plugins[f.Extension].run(con);
			} catch {
				Console.WriteLine("No luck, using default.");
				s = this.plugins["*"].run(con);
			}
			
			try {
				Console.WriteLine("Content: {0}\r\nLength: {1}", con.Request.RawUrl, s.Length);
				con.Response.ContentLength64 = s.Length;
				while(s.Position < s.Length) {
					byte[] buf = new byte[1024];
					int bRead = s.Read(buf, 0, buf.Length);
					con.Response.OutputStream.Write(buf, 0, bRead);
				}
			} catch(Exception e) {
				Console.WriteLine(e);
			}
			finally {
				s.Close();
				con.Response.Close();
			}
		}
		protected void cb(IAsyncResult result) {
			HttpListenerContext con = _listener.EndGetContext(result);
			
			cb(con);
		}
		
		public void Start() {
			this._listener.Start();
			this.t.Start();
		}
		
		public void Stop() {
			this._listener.Close();
			this.t.Abort();
		}
	}
	
	#region tools
	
	class Path {
		public static string rootDir = @"c:\users\snoj\documents\projects\poshttpd\sites\";
		
		static System.IO.FileInfo getPath(string RawUrl) {
			string fp = RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}
			
			return new System.IO.FileInfo(fp);
		}
		public static System.IO.FileInfo getABSPath(string RawUrl) {
			string fp = RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}
			
			return new System.IO.FileInfo(System.IO.Path.Combine(new string[] {Path.rootDir, fp.TrimStart("\\/".ToCharArray())}));
		}
	}
	
	class HttpListenerResponseClone {
		public HttpListenerResponse Response;
		public HttpListenerResponseClone(HttpListenerResponse Response) {
			this.Response = Response;
		}
		
		public HttpListenerResponse Clone() {
			return ((HttpListenerResponseClone)this.MemberwiseClone()).Response;
		}
	}
	
	#endregion
	
	#region engines
	public interface IEngine {
		System.IO.Stream run(HttpListenerContext context);
		//HttpListenerResponse getResponse();
	}
	///<Summary>
	///
	///</Summary>
	public class PS : IEngine {
		string rootDir = @"c:\users\snoj\documents\projects\poshttpd\sites\";
		//HttpListenerResponse nresponse;
		public System.IO.Stream run(HttpListenerContext context) {
			try {
				PowerShell shell = PowerShell.Create();
				
				shell.AddCommand(Path.getABSPath(context.Request.RawUrl).FullName);
				
				//doesn't work.
				//HttpListenerResponseClone tmp = new HttpListenerResponseClone(context.Response);
				//nresponse = tmp.Clone();
				
				//shell.AddParameter("Request", context.Request);
				//shell.AddParameter("Response", nresponse);
				//shell.AddParameter("User", context.User);
				
				shell.AddParameter("Context", context);
				
				System.Collections.ObjectModel.Collection<PSObject> rtn = shell.Invoke();
				
				System.IO.MemoryStream ms = new System.IO.MemoryStream();
				
				
				foreach(PSObject r in rtn) {
					if(r.BaseObject.GetType().Equals(typeof(string))) {
						byte[] buf = System.Text.Encoding.UTF8.GetBytes(r.ToString() + Environment.NewLine);
						ms.Write(buf, 0 , buf.Length);
					}
					
					//if(r.BaseObject.Equals(typeof(HttpListenerResponse))) {
						//nresponse = (HttpListenerResponse)r.BaseObject;
					//}
				}
				ms.Seek(0, System.IO.SeekOrigin.Begin);
				
				return (System.IO.Stream)ms;
			} catch(Exception e) {
				Console.WriteLine(e);
			}
			
			return (System.IO.Stream)new System.IO.MemoryStream(0);
		}
		
		/*public HttpListenerResponse getResponse(){
			return this.nresponse;
		}*/
	}
	
	///<Summary>
	///	Passthru requested files. This will find the file on disk and send it to the client.
	/// Useful for static things like HTML files, images, and the like.
	///</Summary>
	public class PassThru : IEngine {
		string rootDir = @"c:\users\snoj\documents\projects\poshttpd\sites\";
		//HttpListenerResponse nresponse;
		public System.IO.Stream run(HttpListenerContext context) {
			Console.WriteLine(context.Request.RawUrl);
			//this.nresponse = context.Response;
			string fp = context.Request.RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}
			
			string absfp = System.IO.Path.Combine(new string[] { this.rootDir, fp.Trim('/') });
			//Console.WriteLine("Checking {0}", absfp);
			if(System.IO.File.Exists(absfp)) {
				try {
					//Console.WriteLine("Opening {0}", absfp);
					return (System.IO.Stream)new System.IO.FileStream(absfp, System.IO.FileMode.Open, System.IO.FileAccess.Read);
				} catch(Exception e) {
					return (System.IO.Stream)new System.IO.MemoryStream(0);
				}
			}
			
			return (System.IO.Stream)new System.IO.MemoryStream(0);
		}
		
		
		/*public HttpListenerResponse getResponse(){
			return this.nresponse;
		}*/
	}
	#endregion
}