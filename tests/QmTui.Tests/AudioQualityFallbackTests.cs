using QmTui.Models;
using Xunit;

namespace QmTui.Tests;

public class AudioQualityFallbackTests
{
    [Fact]
    public void Master_ShouldFallbackSequentially()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.Master);
        Assert.Equal(
            [AudioQualityTier.Atmos, AudioQualityTier.Dolby, AudioQualityTier.Premium, AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
            fallbacks);
    }

    [Fact]
    public void AtmosAndDolbyAndPremium_ShouldFallbackStepByStep()
    {
        Assert.Equal(
            [AudioQualityTier.Dolby, AudioQualityTier.Premium, AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
            AudioQualityHelper.GetFallbackTiers(AudioQualityTier.Atmos));

        Assert.Equal(
            [AudioQualityTier.Premium, AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
            AudioQualityHelper.GetFallbackTiers(AudioQualityTier.Dolby));

        Assert.Equal(
            [AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
            AudioQualityHelper.GetFallbackTiers(AudioQualityTier.Premium));
    }

    [Fact]
    public void HiRes_ShouldFallbackToSQ_HQ_Standard()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.HiRes);
        Assert.Equal([AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard], fallbacks);
    }

    [Fact]
    public void SQ_ShouldFallbackToHQ_Standard()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.SQ);
        Assert.Equal([AudioQualityTier.HQ, AudioQualityTier.Standard], fallbacks);
    }

    [Fact]
    public void QualityOption_DisplayText_UnavailableShouldShowNotice()
    {
        var option = new QualityOption(AudioQualityTier.Master, "Master", "臻品母带", "24bit/192kHz", "", false);
        var text = option.DisplayText(false);
        Assert.Contains("(无音源)", text);
    }

    [Fact]
    public void QualityOption_DisplayText_AvailableShouldShowNormal()
    {
        var option = new QualityOption(AudioQualityTier.SQ, "SQ", "SQ 无损", "16bit/44.1kHz", "850kbps", true, null, 61839633);
        var text = option.DisplayText(true);
        Assert.DoesNotContain("无音源", text);
        Assert.Contains("59.0MB", text);
        Assert.Contains("{850kbps}", text);
        Assert.Contains("✓", text);
    }

    [Theory]
    [InlineData(0L, "")]
    [InlineData(500L, "500B")]
    [InlineData(2048L, "2.0KB")]
    [InlineData(10485760L, "10.0MB")]
    [InlineData(61839633L, "59.0MB")]
    [InlineData(100164810L, "95.5MB")]
    [InlineData(1073741824L, "1.0GB")]
    public void QualityOption_FormatFileSize_FormatsCorrectly(long bytes, string expected)
    {
        var formatted = QualityOption.FormatFileSize(bytes);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_FlacExtension_ShouldReturnSQ()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("标准 128k", "/music/song.flac");
        Assert.Equal(AudioQualityTier.SQ, tier);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_HiResFlac_ShouldReturnHiRes()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("Hi-Res 24bit", "/music/song.flac");
        Assert.Equal(AudioQualityTier.HiRes, tier);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_Mp3320_ShouldReturnHQ()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("HQ 320k", "/music/song.mp3");
        Assert.Equal(AudioQualityTier.HQ, tier);
    }

    [Fact]
    public void ParseProbedQualities_HiResWithSampleRate_ResolvesAsAvailableAndFormatsSpec()
    {
        var requests = new (string Key, AudioQualityTier Tier, string Prefix, string Extension)[]
        {
            ("req_hires", AudioQualityTier.HiRes, "RS01", ".flac"),
            ("req_sq", AudioQualityTier.SQ, "F000", ".flac")
        };

        var json = """
        {
            "songinfo": {
                "data": {
                    "track_info": {
                        "interval": 240,
                        "file": {
                            "size_flac": 30000000,
                            "hires_sample": 96000,
                            "hires_bitdepth": 24
                        }
                    }
                }
            },
            "req_hires": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "RS010039MnYb0qxYhV.flac?vkey=test", "result": 0 }
                    ]
                }
            },
            "req_sq": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "F0000039MnYb0qxYhV.flac?vkey=test", "result": 0 }
                    ]
                }
            }
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var options = QmTui.Api.MusicApi.ParseProbedQualities(doc.RootElement, requests);

        var hiResOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.HiRes);
        Assert.NotNull(hiResOpt);
        Assert.True(hiResOpt.Available);
        Assert.Equal("24bit / 96kHz", hiResOpt.Spec);
        Assert.Equal("https://isure.stream.qqmusic.qq.com/RS010039MnYb0qxYhV.flac?vkey=test", hiResOpt.PlayUrl);
    }

    [Fact]
    public void ParseProbedQualities_HiResWithSizeHires_ResolvesAsAvailable()
    {
        var requests = new (string Key, AudioQualityTier Tier, string Prefix, string Extension)[]
        {
            ("req_hires", AudioQualityTier.HiRes, "RS01", ".flac")
        };

        var json = """
        {
            "songinfo": {
                "data": {
                    "track_info": {
                        "interval": 200,
                        "file": {
                            "size_hires": 48000000
                        }
                    }
                }
            },
            "req_hires": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "RS010039MnYb0qxYhV.flac?vkey=test", "result": 0 }
                    ]
                }
            }
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var options = QmTui.Api.MusicApi.ParseProbedQualities(doc.RootElement, requests);

        var hiResOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.HiRes);
        Assert.NotNull(hiResOpt);
        Assert.True(hiResOpt.Available);
        Assert.Equal("https://isure.stream.qqmusic.qq.com/RS010039MnYb0qxYhV.flac?vkey=test", hiResOpt.PlayUrl);
    }

    [Fact]
    public void ParseProbedQualities_SizeNew11Only_DoesNotActivateHiRes()
    {
        var requests = new (string Key, AudioQualityTier Tier, string Prefix, string Extension)[]
        {
            ("req_hires", AudioQualityTier.HiRes, "RS01", ".flac")
        };

        var json = """
        {
            "songinfo": {
                "data": {
                    "track_info": {
                        "interval": 200,
                        "file": {
                            "size_new": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 48000000]
                        }
                    }
                }
            },
            "req_hires": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "RS010039MnYb0qxYhV.flac?vkey=test", "result": 0 }
                    ]
                }
            }
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var options = QmTui.Api.MusicApi.ParseProbedQualities(doc.RootElement, requests);

        var hiResOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.HiRes);
        Assert.NotNull(hiResOpt);
        Assert.False(hiResOpt.Available);
    }

    [Fact]
    public void ParseProbedQualities_FalseHiRes_RejectsHiResAndKeepsSQAvailable()
    {
        var requests = new (string Key, AudioQualityTier Tier, string Prefix, string Extension)[]
        {
            ("req_hires", AudioQualityTier.HiRes, "RS01", ".flac"),
            ("req_sq", AudioQualityTier.SQ, "F000", ".flac")
        };

        // 场景：只有普通的 SQ (size_flac)，没有 size_hires / 采样率 / size_new[11]，即便网关盲签了 RS01 purl，也应当判定 HiRes 不可用
        var json = """
        {
            "songinfo": {
                "data": {
                    "track_info": {
                        "interval": 200,
                        "file": {
                            "size_flac": 28000000,
                            "size_hires": 0,
                            "hires_sample": 0,
                            "hires_bitdepth": 0,
                            "size_new": [170000000, 27000000, 71000000, 9700000, 0, 20000000, 32000000, 2200000, 27000000, 6700000, 0, 0, 0, 0, 0, 0]
                        }
                    }
                }
            },
            "req_hires": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "RS01001ToGjY158HHH.flac?vkey=test", "result": 0 }
                    ]
                }
            },
            "req_sq": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "F000001ToGjY158HHH.flac?vkey=test", "result": 0 }
                    ]
                }
            }
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var options = QmTui.Api.MusicApi.ParseProbedQualities(doc.RootElement, requests);

        var hiResOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.HiRes);
        var sqOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.SQ);

        Assert.NotNull(hiResOpt);
        Assert.False(hiResOpt.Available); // 必须被识别为不可用

        Assert.NotNull(sqOpt);
        Assert.True(sqOpt.Available); // SQ 正常可用
        Assert.Equal("https://isure.stream.qqmusic.qq.com/F000001ToGjY158HHH.flac?vkey=test", sqOpt.PlayUrl);
    }

    [Fact]
    public async Task ProbeSongQualitiesAsync_MousouKanshouDaishouRenmei_ResolvesCorrectHiResSize()
    {
        UserSession.Load();
        var options = await QmTui.Api.MusicApi.ProbeSongQualitiesAsync("000qiToY03CMRk", "001BZWuO0EAbYH");
        var hiRes = options.FirstOrDefault(o => o.Tier == AudioQualityTier.HiRes);
        var sq = options.FirstOrDefault(o => o.Tier == AudioQualityTier.SQ);

        Assert.NotNull(sq);
        Assert.True(sq.FileSizeBytes > 50_000_000); // SQ 约 61.8MB

        if (hiRes != null && hiRes.Available)
        {
            Assert.True(hiRes.FileSizeBytes > sq.FileSizeBytes);
            Assert.Equal("95.5MB", QualityOption.FormatFileSize(hiRes.FileSizeBytes));
            Assert.Contains("2968kbps", hiRes.BitrateInfo);
        }
    }

    [Fact]
    public void ParseProbedQualities_DolbyZeroSize_RejectsDolbyEvenIfPurlExists()
    {
        var requests = new (string Key, AudioQualityTier Tier, string Prefix, string Extension)[]
        {
            ("req_dolby", AudioQualityTier.Dolby, "Q000", ".flac")
        };

        // 当 size_dolby 为 0 时，即便 size_new[3] (OGG) 有值且接口返回了 purl，也不应误判为可用杜比
        var json = """
        {
            "songinfo": {
                "data": {
                    "track_info": {
                        "interval": 200,
                        "file": {
                            "size_dolby": 0,
                            "size_new": [0, 0, 0, 13123456]
                        }
                    }
                }
            },
            "req_dolby": {
                "data": {
                    "sip": ["https://isure.stream.qqmusic.qq.com/"],
                    "midurlinfo": [
                        { "purl": "Q0000039MnYb0qxYhV.flac?vkey=test", "result": 0 }
                    ]
                }
            }
        }
        """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var options = QmTui.Api.MusicApi.ParseProbedQualities(doc.RootElement, requests);

        var dolbyOpt = options.FirstOrDefault(o => o.Tier == AudioQualityTier.Dolby);
        Assert.NotNull(dolbyOpt);
        Assert.False(dolbyOpt.Available);
    }

    [Fact]
    public void TryParseFlacStreamInfo_Valid48k24bitHeader_ParsesSuccessfully()
    {
        // 构造标准的 42 字节 FLAC 头部，包含 24bit / 48kHz 立体声 STREAMINFO
        var header = new byte[42];
        header[0] = (byte)'f';
        header[1] = (byte)'L';
        header[2] = (byte)'a';
        header[3] = (byte)'C';
        header[4] = 0x00; // BlockType = 0 (STREAMINFO)
        header[5] = 0x00;
        header[6] = 0x00;
        header[7] = 0x22; // Length = 34

        // 48000 Hz = 0x0BB80 (20 bits)
        // byte 18: 0x0B (高 8 位)
        // byte 19: 0xB8 (中 8 位)
        // byte 20: bits 7..4 = 0x0 (低 4 位)
        //          bits 3..1 = channels - 1 = 1 (立体声 2ch -> 0b001 -> 0x02)
        //          bit 0     = (bits_per_sample - 1) 的高 1 位 = (23 >> 4) = 1 (0b0001)
        //          因此 byte 20 = (0x0 << 4) | (1 << 1) | 1 = 0x03
        // byte 21: bits 7..4 = (bits_per_sample - 1) 的低 4 位 = (23 & 0x0F) = 7 (0b0111) -> 0x70
        header[18] = 0x0B;
        header[19] = 0xB8;
        header[20] = 0x03;
        header[21] = 0x70;

        var success = AudioQualityHelper.TryParseFlacStreamInfo(header, out var sampleRate, out var bitsPerSample, out var channels);
        Assert.True(success);
        Assert.Equal(48000, sampleRate);
        Assert.Equal(24, bitsPerSample);
        Assert.Equal(2, channels);

        var spec = AudioQualityHelper.FormatAudioSpec(sampleRate, bitsPerSample, channels, AudioQualityTier.SQ);
        Assert.Equal("24bit / 48kHz", spec);
    }

    [Fact]
    public void FormatAudioSpec_6ChannelAtmos_FormatsAsSurround()
    {
        var spec = AudioQualityHelper.FormatAudioSpec(44100, 16, 6, AudioQualityTier.Atmos);
        Assert.Equal("5.1 环绕声", spec);
    }
}

