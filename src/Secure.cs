using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RelayLite {
// Current-user DPAPI protects new secrets. Legacy Electron v10 AES-GCM blobs are
// read only from a copied store and re-encrypted before the new store is saved.
internal sealed class Secure {
    static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Codex Relay Lite / credentials / v1");
    readonly byte[] legacyKey;
    public Secure(string legacyLocalState=null) {
        if(legacyLocalState!=null&&File.Exists(legacyLocalState)) {
            var state=J.Read(legacyLocalState,null);string text=J.Str(J.Get(state,"os_crypt"),"encrypted_key");
            if(text!="") {var raw=Convert.FromBase64String(text);if(raw.Length<=5||Encoding.ASCII.GetString(raw,0,5)!="DPAPI")throw J.Error("旧版密钥格式不受支持。");var encrypted=new byte[raw.Length-5];Buffer.BlockCopy(raw,5,encrypted,0,encrypted.Length);legacyKey=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);}
        }
    }
    public string Encrypt(string value) {return "dpapi1:"+Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),Entropy,DataProtectionScope.CurrentUser));}
    public string Decrypt(string value) {
        if(string.IsNullOrEmpty(value))return "";
        if(value.StartsWith("dpapi1:",StringComparison.Ordinal))return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(7)),Entropy,DataProtectionScope.CurrentUser));
        byte[] raw=Convert.FromBase64String(value);
        if(raw.Length>31&&new[]{"v10","v11"}.Contains(Encoding.ASCII.GetString(raw,0,3))) {
            if(legacyKey==null)throw J.Error("缺少旧版加密信息，请从完整的旧版数据目录迁移。");
            var nonce=new byte[12];Buffer.BlockCopy(raw,3,nonce,0,12);var tag=new byte[16];Buffer.BlockCopy(raw,raw.Length-16,tag,0,16);var cipher=new byte[raw.Length-31];Buffer.BlockCopy(raw,15,cipher,0,cipher.Length);return Encoding.UTF8.GetString(GcmDecrypt(legacyKey,nonce,cipher,tag));
        }
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw,null,DataProtectionScope.CurrentUser));
    }
    [StructLayout(LayoutKind.Sequential)] struct AuthInfo { public int Size;public int Version;public IntPtr Nonce;public int NonceLength;public IntPtr AuthData;public int AuthLength;public IntPtr Tag;public int TagLength;public IntPtr Mac;public int MacLength;public int Aad;public long Data;public int Flags; }
    [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg,string name,string impl,int flags);
    [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr handle,string property,byte[] value,int length,int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg,out IntPtr key,IntPtr obj,int objLen,byte[] secret,int secretLen,int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key,byte[] input,int inputLen,ref AuthInfo info,IntPtr iv,int ivLen,byte[] output,int outputLen,out int result,int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
    [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr alg,int flags);
    static void Check(int code) {if(code!=0)throw J.Error("Windows 密钥解密失败，旧数据未修改。");}
    public static byte[] GcmDecrypt(byte[] key,byte[] nonce,byte[] cipher,byte[] tag) {
        IntPtr alg=IntPtr.Zero,handle=IntPtr.Zero;GCHandle noncePin=default(GCHandle),tagPin=default(GCHandle);
        try {Check(BCryptOpenAlgorithmProvider(out alg,"AES",null,0));byte[] mode=Encoding.Unicode.GetBytes("ChainingModeGCM\0");Check(BCryptSetProperty(alg,"ChainingMode",mode,mode.Length,0));Check(BCryptGenerateSymmetricKey(alg,out handle,IntPtr.Zero,0,key,key.Length,0));noncePin=GCHandle.Alloc(nonce,GCHandleType.Pinned);tagPin=GCHandle.Alloc(tag,GCHandleType.Pinned);var info=new AuthInfo {Size=Marshal.SizeOf(typeof(AuthInfo)),Version=1,Nonce=noncePin.AddrOfPinnedObject(),NonceLength=nonce.Length,Tag=tagPin.AddrOfPinnedObject(),TagLength=tag.Length};byte[] output=new byte[cipher.Length];int size;Check(BCryptDecrypt(handle,cipher,cipher.Length,ref info,IntPtr.Zero,0,output,output.Length,out size,0));return output;}
        finally {if(noncePin.IsAllocated)noncePin.Free();if(tagPin.IsAllocated)tagPin.Free();if(handle!=IntPtr.Zero)BCryptDestroyKey(handle);if(alg!=IntPtr.Zero)BCryptCloseAlgorithmProvider(alg,0);}
    }
}
}
