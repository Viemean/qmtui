#!/usr/bin/env python3
import os
import re
import sys

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TARGET_DIR = os.path.join(BASE_DIR, "src", "Services", "AudioRecognition")

RAW_EXTRACTOR = os.path.join(TARGET_DIR, "AcousticFingerprintExtractor.raw.cs")
OUT_EXTRACTOR = os.path.join(TARGET_DIR, "AcousticFingerprintExtractor.cs")

RAW_CLIENT = os.path.join(TARGET_DIR, "AcousticRecognizeClient.raw.cs")
OUT_CLIENT = os.path.join(TARGET_DIR, "AcousticRecognizeClient.cs")

def strip_csharp_comments(source: str) -> str:
    out = []
    i = 0
    n = len(source)
    in_string = False
    in_char = False
    is_verbatim = False
    
    while i < n:
        c = source[i]
        
        if in_string:
            out.append(c)
            if is_verbatim:
                if c == "\"" and i + 1 < n and source[i+1] == "\"":
                    out.append(source[i+1])
                    i += 2
                    continue
                elif c == "\"":
                    in_string = False
            else:
                if c == "\\":
                    if i + 1 < n:
                        out.append(source[i+1])
                        i += 2
                        continue
                elif c == "\"":
                    in_string = False
            i += 1
            continue
            
        if in_char:
            out.append(c)
            if c == "\\":
                if i + 1 < n:
                    out.append(source[i+1])
                    i += 2
                    continue
            elif c == "\x27":
                in_char = False
            i += 1
            continue
            
        if c == "@" and i + 1 < n and source[i+1] == "\"":
            out.append("@\"")
            in_string = True
            is_verbatim = True
            i += 2
            continue
        elif c == "\"":
            out.append(c)
            in_string = True
            is_verbatim = False
            i += 1
            continue
        elif c == "\x27":
            out.append(c)
            in_char = True
            i += 1
            continue
            
        if c == "/" and i + 1 < n:
            if source[i+1] == "/":
                i += 2
                while i < n and source[i] != "\n":
                    i += 1
                continue
            elif source[i+1] == "*":
                i += 2
                while i + 1 < n and not (source[i] == "*" and source[i+1] == "/"):
                    i += 1
                i += 2
                continue
                
        out.append(c)
        i += 1
        
    # 清理多余连续空行
    lines = "".join(out).splitlines()
    cleaned = []
    prev_empty = False
    for line in lines:
        stripped = line.rstrip()
        if not stripped:
            if not prev_empty:
                cleaned.append("")
                prev_empty = True
        else:
            cleaned.append(stripped)
            prev_empty = False
    return "\n".join(cleaned)

def xor_bytes(text: str, key: int = 0x73) -> str:
    encoded = [b ^ key for b in text.encode('utf-8')]
    return f"[{', '.join(str(b) for b in encoded)}]"

def transform_client(code: str) -> str:
    # 1. 优先替换端点常量，避免明文 URL 泄漏
    ep_bytes = xor_bytes("http://c.y.qq.com/youtu/humming/search", 0x73)
    code = re.sub(
        r'private const string Endpoint\s*=\s*"http://c\.y\.qq\.com/youtu/humming/search";',
        f'private static readonly byte[] _eData = {ep_bytes};\n    private static string _ep => _dStr(_eData, 0x73);',
        code
    )

    # 2. 剥离注释
    code = strip_csharp_comments(code)

    # 3. 混淆请求头与特征参数
    ua_bytes = xor_bytes("MusicRecognition 34}(android 10)", 0x73)
    ck_bytes = xor_bytes("uin=; ct=3003; cv=10306; recognizetype=1", 0x73)
    aid_key_bytes = xor_bytes("AppId", 0x73)
    aid_val_bytes = xor_bytes("85", 0x73)

    replacements = [
        ("s_cachedChannelConfig", "_cc"),
        ("s_configLock", "_lk"),
        ("s_obfSource", "_os"),
        ("s_obfSalt", "_ot"),
        ("s_obfAesKey", "_ok"),
        ("GetChannelConfig", "_gc"),
        ("Deobfuscate", "_dob"),
        ("ProtocolVersion", "_pv"),
        ("Endpoint", "_ep"),
    ]
    for old, new in replacements:
        code = re.sub(r'\b' + old + r'\b', new, code)

    code = code.replace('"MusicRecognition 34}(android 10)"', '_dStr(_uaData, 0x73)')
    code = code.replace('"AppId", "85"', '_dStr(_akData, 0x73), _dStr(_avData, 0x73)')
    code = code.replace('"uin=; ct=3003; cv=10306; recognizetype=1"', '_dStr(_ckData, 0x73)')

    helper_def = f"""    private static readonly byte[] _uaData = {ua_bytes};
    private static readonly byte[] _ckData = {ck_bytes};
    private static readonly byte[] _akData = {aid_key_bytes};
    private static readonly byte[] _avData = {aid_val_bytes};

    private static string _dStr(byte[] b, byte k)
    {{
        var res = new byte[b.Length];
        for (int i = 0; i < b.Length; i++) res[i] = (byte)(b[i] ^ k);
        return Encoding.UTF8.GetString(res);
    }}
"""
    code = code.replace("public static class AcousticRecognizeClient\n{", f"public static class AcousticRecognizeClient\n{{\n{helper_def}")
    
    header = "// <auto-generated>\n// Internal optimized acoustic network transport kernel\n// </auto-generated>\n#nullable enable\n"
    return header + code.strip() + "\n"

def transform_extractor(code: str) -> str:
    code = strip_csharp_comments(code)

    replacements = [
        ("s_hammingWindow", "_hW"),
        ("s_bitRevSwaps", "_brS"),
        ("s_twiddles", "_tw"),
        ("s_modeBitWidths", "_mbw"),
        ("s_modeItemCounts", "_mic"),
        ("s_modePaddingBits", "_mpb"),
        ("s_maxDiffs", "_mxd"),
        ("InitHammingWindow", "_inHw"),
        ("InitBitRevSwaps", "_inBrs"),
        ("InitTwiddles", "_inTw"),
        ("ComputeFft", "_cFft"),
        ("BitWriter", "_bWr"),
        ("WriteBits", "_wB"),
        ("ToBytes", "_tB"),
        ("ExtractPeaks", "_exPk"),
    ]
    for old, new in replacements:
        code = re.sub(r'\b' + old + r'\b', new, code)

    header = "// <auto-generated>\n// High performance acoustic stream transformation pipeline\n// </auto-generated>\n#nullable enable\n"
    return header + code.strip() + "\n"

def sync():
    if not os.path.exists(RAW_EXTRACTOR) or not os.path.exists(RAW_CLIENT):
        return 0

    with open(RAW_CLIENT, 'r', encoding='utf-8') as f:
        client_raw = f.read()
    client_transformed = transform_client(client_raw)
    
    if not os.path.exists(OUT_CLIENT) or open(OUT_CLIENT, 'r', encoding='utf-8').read() != client_transformed:
        with open(OUT_CLIENT, 'w', encoding='utf-8') as f:
            f.write(client_transformed)
        print("Generated acoustic recognition transport kernel")

    with open(RAW_EXTRACTOR, 'r', encoding='utf-8') as f:
        extractor_raw = f.read()
    extractor_transformed = transform_extractor(extractor_raw)
    
    if not os.path.exists(OUT_EXTRACTOR) or open(OUT_EXTRACTOR, 'r', encoding='utf-8').read() != extractor_transformed:
        with open(OUT_EXTRACTOR, 'w', encoding='utf-8') as f:
            f.write(extractor_transformed)
        print("Generated acoustic fingerprint pipeline kernel")

    return 0

if __name__ == "__main__":
    sys.exit(sync())
