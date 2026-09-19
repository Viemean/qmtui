# Maintainer: Yuzuki <lxf74663@gmail.com>

pkgname=qmtui-bin
_pkgname=qmtui
pkgver=0.3.5
pkgrel=1
pkgdesc="Linux terminal qqmusic player (.NET 10 Native AOT pre-built binary package)"
arch=('x86_64' 'aarch64')
url="https://github.com/Viemean/qmtui"
license=('MIT')

depends=(
    'gstreamer'
    'gst-plugins-base'
    'gst-plugins-good'
    'gst-plugins-bad'
    'libpulse'
)

optdepends=(
    'gst-libav: additional audio codecs (AAC/M4A) support'
    'wl-clipboard: Wayland clipboard support for copying song links'
    'xclip: X11 clipboard support for copying song links'
)

provides=('qmtui' 'qqmusic-tui' 'qmtui-bin')
conflicts=('qmtui' 'qqmusic-tui' 'qqmusic-tui-bin')
replaces=('qqmusic-tui' 'qqmusic-tui-bin')

package() {
    local _bin_src="${srcdir}/../bin/Release/net10.0/linux-x64/publish/qmtui"
    local _www_src="${srcdir}/../bin/Release/net10.0/linux-x64/publish/www"

    install -dm755 "${pkgdir}/usr/bin"
    install -dm755 "${pkgdir}/usr/share/qmtui"

    if [ -f "${_bin_src}" ]; then
        install -Dm755 "${_bin_src}" "${pkgdir}/usr/bin/${_pkgname}"
        ln -sf "${_pkgname}" "${pkgdir}/usr/bin/qqmusic-tui"
    fi

    if [ -d "${_www_src}" ]; then
        cp -r "${_www_src}" "${pkgdir}/usr/share/qmtui/"
    fi
}
