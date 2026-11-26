"""Compile native bundle-cache entry points against inert assets; no game, server or network.

python verify-bundle-lock.py [--root /checkout] [--dotnet /path/to/dotnet] [--stock-control]
--stock-control additionally compiles the unmodified v2.11.0 lock paths from local Git objects
and requires the exact duplicate-key failure under the same deterministic two-thread schedule.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile

BASE = 'c7ffab0db7106ed55a008ed63a9acd60bd8c392a'
AB = 'Assets/Scrips/PCRCalcularor/ABExTool.cs'
ASSET = 'PCRCalculatorServer/UnityEngine/AssetBundle.cs'


def method(src, signature):
    # Upstream methods are indented exactly eight spaces; inner scopes are deeper.
    m = re.search(r'^        ' + re.escape(signature) + r'[^\n]*\n        \{\n.*?^        \}', src, re.M|re.S)
    if not m: raise ValueError(f'cannot extract production method: {signature}')
    return m[0]


def line(src, pattern):
    found = re.findall(pattern, src, re.M)
    if len(found) != 1: raise ValueError(f'expected one production declaration: {pattern}')
    return found[0]


def project(ab, asset):
    # Pull the SAME production methods; only disk, manifest, serialized object data and decoder below are fakes.
    direct = method(ab, 'public static AssetBundle TryGetAssetBundleByNameWithLock(')
    shared = method(ab, 'public static AssetBundle TryGetAssetBundleByName(')
    deps = method(asset, 'private void LoadDeps()')
    get = method(asset, 'internal T LoadAsset<T>(')
    mem = method(asset, 'internal static AssetBundle LoadFromMemory(')
    assert 'lock (loadBundleLock)' in direct and 'TryGetAssetBundleByName(fullname, useOldManifest)' in direct
    assert 'AssetBundleDic.Add(fullname, asset);' in shared and 'AssetBundleDic.TryGetValue(fullname' in shared
    assert 'ABExTool.TryGetAssetBundleByName(depName, false)' in deps and 'lock (loaderLock)' in get and 'LoadDeps();' in get
    assert 'lock (loaderLock)' in mem and 'loadedBundle[result.name] = result;' in mem
    for sig in ('private static void CompactServerBattleunitBundleData(', 'private static void CompactServerBattleunitDependencies('):
        s = method(ab, sig); assert 'lock (loadBundleLock)' in s and 'AssetBundleDic.Remove(' in s
    for sig in ('internal static bool TryCompactServerBattleunitBundleData(', 'internal static string[] TryCompactServerBattleunitDependencies('):
        assert 'lock (loaderLock)' in method(asset, sig)
    begin = '''using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System;
using System.IO;
using System.Threading.Tasks;
class ABExTool {
'''
    stub = '''
    static readonly object mgrDataJP=new(), mgrCharacter=new();
    static void LoadDependency(string name, byte[] bytes, object mgr) {} // also a no-op in headless
    static string GetBundleCachePath(string n, object m) => throw new Exception("unexpected disk cache");
    static byte[] ReadOrRepairBundleCacheFile(string n, object m) => throw new Exception("unexpected disk cache");
    static bool IsExpectedBundleBytes(string n, byte[] b, object m, string s) => true;
}
class MainManager { public static MainManager Instance = new(); public MainManager Version => this; public bool newAB => false; }
class Application { public static string streamingAssetsPath => "/dummy-read-only"; }
class Debug { public static void LogWarning(string s) => throw new Exception(s); }
class WWW {
    public bool isDone => true;
    public byte[] bytes => Array.Empty<byte>();
    public AssetBundle getAssetBundle() => AssetBundle.NewSyntheticBundle();
    public AssetBundle assetBundle => getAssetBundle();
    public WWW(string url) { if (Sc.Direct) { Sc.AfterMiss.Set(); Sc.Wait(Sc.MayPublish); } }
}
class Object {}
class SData : IEnumerable<SData> {
    public string AsString => Sc.Key;
    public SData this[string name] => this;
    public IEnumerator<SData> GetEnumerator() { yield return this; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
class AssetClassID { public const int AssetBundle=0; }
class FakeImpl {
    public string name = "synthetic-cab";
    public FakeImpl file => this; public FakeImpl Metadata => this;
    public (string PathName, int x)[] Externals => [("archive:/synthetic-cab/synthetic-cab",0)];
    public int[] GetAssetsOfType(int t) => [0];
    public SData GetBaseField(FakeImpl i,int j) => new();
}
class AssetBundle {
    public static object TestGate => loaderLock;
    public AssetBundle[] dependencies;
    private string name="synthetic-owner";
    private FakeImpl impl=new(); private static FakeImpl am=new();
    private static Dictionary<string,AssetBundle> loadedBundle=new();
    private Dictionary<string,List<long>> pathMap=new() {{"test",[0]}};
    // The parser does no I/O. Match reviewed LoadFromMemory guard and its loadedBundle publication;
    // compare with the real implementation above, instead of initializing AssetsTools / Unity.
    public static AssetBundle NewSyntheticBundle() { lock(loaderLock) {
        Interlocked.Increment(ref Sc.Decodes); var result=new AssetBundle { name=Sc.Key };
        loadedBundle[result.name] = result; return result;
    } }
    public static void Reset() { loadedBundle.Clear(); }
    static class ObjectManager { public static T Deserialize<T>(long id, AssetBundle bundle) where T:Object => null; }
'''
    schedule = '''
}
class Sc {
    public const string Key="all_battleunit_133001.unity3d";
    public static int Decodes;
    [ThreadStatic] public static bool Direct;
    public static ManualResetEventSlim AfterMiss = new(false), MayPublish = new(false);
    public static void Wait(ManualResetEventSlim e) { if (!e.Wait(4000)) throw new TimeoutException("fixture coordination"); }
    static Task Lane(Action a) => Task.Factory.StartNew(a,System.Threading.CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    static void Main(string[] args) {
        bool stock=args.Single()=="stock"; int runs=32;
        for(int i=0;i<runs;i++) {
            AfterMiss.Reset(); MayPublish.Reset(); ABExTool.AssetBundleDic.Clear(); AssetBundle.Reset(); Decodes=0;
            var owner = new AssetBundle(); AssetBundle direct=null; Exception err=null; bool split=false;
            var a=Lane(()=>{ Direct=true; try { direct=ABExTool.TryGetAssetBundleByNameWithLock(Key,false); } catch(Exception ex) { err=ex; } });
            var b=Lane(()=>{ Wait(AfterMiss);
                // Direct lane holds its lock, parked after the real TryGetValue miss, before its strict Add.
                // No sleeps: if the dependency monitor is independent, let that lane publish first.
                split=Monitor.TryEnter(AssetBundle.TestGate);
                if(!split) MayPublish.Set();
                try { owner.LoadAsset<Object>("test"); } finally { if(split) Monitor.Exit(AssetBundle.TestGate); MayPublish.Set(); }
            });
            if(!Task.WaitAll([a,b],8000)) throw new TimeoutException("load paths deadlocked");
            if(split!=stock || ABExTool.AssetBundleDic.Count!=1 || Decodes!=(stock?2:1)) throw new Exception("unexpected decode/register count or monitor identity");
            if(stock) { if(err is not ArgumentException || !err.Message.Contains(Key)) throw new Exception("stock control did not produce the duplicate-key exception",err); }
            else if(err!=null || !ReferenceEquals(owner.dependencies[1],direct) || !ReferenceEquals(ABExTool.AssetBundleDic[Key],direct)) throw new Exception("published object mismatch",err);
        }
        Console.WriteLine(stock ? $"PASS: {runs} stock split-monitor negative controls: exact duplicate key each time" : $"PASS: {runs} cold interleavings: one decode/Add, both paths return the same object, nested locks complete");
    }
}
'''
    return (begin + line(ab,r'^        public static Dictionary<string, AssetBundle> AssetBundleDic[^\n]+')+'\n'+
        line(ab,r'^        (?:private static object|internal static readonly object) loadBundleLock = new\(\);')+'\n'+direct+'\n'+shared+'\n'+stub+
        line(asset,r'^        private static object loaderLock (?:=|=>)[^\n]+')+'\n'+deps+'\n'+get+'\n'+schedule)


def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[2]); p.add_argument('--dotnet',default='dotnet'); p.add_argument('--stock-control',action='store_true'); args=p.parse_args()
    dotnet=args.dotnet if args.dotnet=='dotnet' else str(Path(args.dotnet).resolve())
    env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_NOLOGO='1')
    for stock in ([True,False] if args.stock_control else [False]):
        def source(n):
            b=subprocess.check_output(['git','-C',str(args.root),'cat-file','blob',BASE+':'+n]) if stock else (args.root/n).read_bytes()
            return b.decode('utf-8-sig').replace('\r\n','\n')
        with tempfile.TemporaryDirectory(prefix='pcr-asset-lock-probe-') as folder:
            d=Path(folder); (d/'Program.cs').write_text(project(source(AB),source(ASSET)),encoding='utf-8'); (d/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><DefineConstants>ENABLE_THREADING</DefineConstants></PropertyGroup></Project>'); (d/'NuGet.Config').write_text('<configuration><packageSources><clear/></packageSources></configuration>')
            subprocess.run([dotnet,'build','-c','Release','-p:UseSharedCompilation=false','--verbosity','quiet'],cwd=d,env=env,check=True,timeout=120)
            subprocess.run([dotnet,str(d/'bin/Release/net8.0/Probe.dll'),'stock' if stock else 'fixed'],cwd=d,env=env,check=True,timeout=45)
if __name__=='__main__': main()
