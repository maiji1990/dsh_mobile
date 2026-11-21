#!/usr/bin/env pwsh
# ADR-0014: builds PRoot 5.1.107.90 (unbundled loader) + libtalloc + libandroid-shmem from the pinned upstream sources
# in native/proot/sources.lock with the pinned NDK, for the APK's ABIs, as drop-in replacements for the DSHBox
# jniLibs (same file names, same NEEDED layout, same exports). Output: artifacts/native/proot/<abi>/
#   libproot.so (PIE executable)  libproot-loader.so (static)  libtalloc.so  libandroid-shmem.so
# plus artifacts/native/proot/SHA256SUMS. Mirrors termux-packages' recipes (see ADR-0014 for every deviation).
# NDK lookup = native/dshmobile-native/build.ps1. Fails (never skips) on a missing tool, a hash mismatch or a check.
#   -SourceCache <dir>   use / fill this directory for the source archives (default native/.generated/proot/src-cache)
#   -Offline             never download; the archives must already be in the cache
param([string]$SourceCache, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandArgumentPassing = 'Standard'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$ndkVersion = '27.0.12077973'
$api = 24   # termux-packages' target API (parity with the DSHBox binaries; libandroid-shmem's code path depends on it)

function Get-Prop([string]$file, [string]$key) {
    if (-not (Test-Path $file)) { return $null }
    $line = Get-Content $file | Where-Object { $_ -match "^$([regex]::Escape($key))=" } | Select-Object -First 1
    if ($line) { return ($line -replace "^$([regex]::Escape($key))=", '') -replace '\\:', ':' -replace '\\\\', '\' }
    return $null
}
$props = Join-Path $repo 'native\local.properties'
$ndk = Get-Prop $props 'ndk.dir'
if (-not $ndk) { $ndk = $env:ANDROID_NDK_HOME }
if (-not $ndk) {
    $sdk = Get-Prop $props 'sdk.dir'
    if (-not $sdk) { $sdk = $env:ANDROID_HOME }
    if ($sdk) { $ndk = Join-Path $sdk "ndk\$ndkVersion" }
}
if (-not $ndk -or -not (Test-Path $ndk)) { throw "NDK $ndkVersion not found (ndk.dir / ANDROID_NDK_HOME / sdk.dir)" }
$hostTag = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'linux-x86_64' }
$bin = Join-Path $ndk "toolchains\llvm\prebuilt\$hostTag\bin"
$exe = if ($hostTag -eq 'windows-x86_64') { '.exe' } else { '' }
$clang = Join-Path $bin "clang$exe"; $readelf = Join-Path $bin "llvm-readelf$exe"; $nm = Join-Path $bin "llvm-nm$exe"
$strip = Join-Path $bin "llvm-strip$exe"
foreach ($t in @($clang, $readelf, $nm, $strip)) { if (-not (Test-Path $t)) { throw "missing NDK tool: $t" } }

function Invoke-Tool([string]$tool, [string[]]$argv) {
    $out = & $tool @argv 2>&1
    if ($LASTEXITCODE -ne 0) { $out | ForEach-Object { Write-Host $_ }; throw "$(Split-Path -Leaf $tool) failed: $($argv -join ' ')" }
    return $out
}

# ---------------------------------------------------------------- sources (pinned URL + SHA-256, never unverified)
$work = Join-Path $repo 'native\.generated\proot'
if (-not $SourceCache) { $SourceCache = Join-Path $work 'src-cache' }
New-Item -ItemType Directory -Force $SourceCache | Out-Null
$src = @{}
# Windows: System32 bsdtar (a GNU tar from Git/MSYS earlier on PATH would read 'D:' as a remote host).
$tar = if ($hostTag -eq 'windows-x86_64') { Join-Path $env:SystemRoot 'System32\tar.exe' } else { 'tar' }
foreach ($line in Get-Content (Join-Path $here 'sources.lock')) {
    if ($line -match '^\s*(#|$)') { continue }
    $name, $version, $urls, $sha, $top, $license = $line.Split('|')
    $urls = $urls.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
    $ext = if ($urls[0].EndsWith('.zip')) { '.zip' } else { '.tar.gz' }
    $archive = Join-Path $SourceCache "$name-$version$ext"
    if (-not (Test-Path $archive)) {
        if ($Offline) { throw "$archive missing and -Offline given" }
        $tmp = "$archive.part"
        $got = $false
        # Upstream first, then the mirrors (sources.lock); the SHA-256 check below applies to whichever answered.
        foreach ($url in $urls) {
            Write-Host "download $url"
            for ($try = 1; $try -le 3 -and -not $got; $try++) {
                try { Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing; $got = $true }
                catch { Write-Host "  try ${try}: $($_.Exception.Message)"; if ($try -lt 3) { Start-Sleep -Seconds (5 * $try) } }
            }
            if ($got) { break }
        }
        if (-not $got) { throw "${name} ${version}: no source URL answered ($($urls -join ', '))" }
        Move-Item -Force $tmp $archive
    }
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha) { throw "$name ${version}: sha256 $actual != pinned $sha ($archive)" }
    $dest = Join-Path $work "src\$name"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    if ($ext -eq '.zip') { Expand-Archive -Path $archive -DestinationPath $dest } else { Invoke-Tool $tar @('-xzf', $archive, '-C', $dest) | Out-Null }
    $root = Join-Path $dest $top
    if (-not (Test-Path $root)) { throw "${name}: $top not found in $archive" }
    $src[$name] = $root
    Write-Host "$name $version  sha256 ok  ($license)"
}
$tallocSrc = $src['talloc']; $shmemSrc = $src['libandroid-shmem']; $prootSrc = Join-Path $src['proot'] 'src'
if (Test-Path (Join-Path $tallocSrc 'config.h')) { throw 'talloc tree unexpectedly has a config.h; compat/talloc/config.h would be shadowed' }

# ADR-0030: 解包 PRoot 源码后按字典序应用本仓库可追溯补丁（使用 GIT_CEILING_DIRECTORIES 防止寻到外层仓库 .git）。
$patchDir = Join-Path $here 'patches'
if (Test-Path $patchDir) {
    $patches = Get-ChildItem -Path $patchDir -Filter '*.patch' -File | Sort-Object Name
    if ($patches.Count -gt 0) {
        $prevCeiling = $env:GIT_CEILING_DIRECTORIES
        try {
            $env:GIT_CEILING_DIRECTORIES = Split-Path -Parent $src['proot']
            foreach ($pf in $patches) {
                Invoke-Tool 'git' @('-C', $src['proot'], 'apply', $pf.FullName) | Out-Null
                Write-Host "proot patch applied: $($pf.Name)"
            }
        } finally {
            $env:GIT_CEILING_DIRECTORIES = $prevCeiling
        }
    }
}

$ldShared = @('-s', '-Wl,--as-needed','-Wl,-z,max-page-size=16384', '-Wl,-z,common-page-size=16384', '-Wl,-z,relro', '-Wl,-z,now',
              '-Wl,-z,noexecstack', '-Wl,--build-id=sha1')
$abis = [ordered]@{ 'arm64-v8a' = 'aarch64-linux-android'; 'x86_64' = 'x86_64-linux-android' }
$sums = [System.Collections.Generic.List[string]]::new()
$outRoot = Join-Path $repo 'artifacts\native\proot'

foreach ($abi in $abis.Keys) {
    $target = "--target=$($abis[$abi])$api"
    $out = Join-Path $outRoot $abi
    $obj = Join-Path $work "obj\$abi"
    foreach ($d in @($out, $obj)) { if (Test-Path $d) { Remove-Item -Recurse -Force $d }; New-Item -ItemType Directory -Force $d | Out-Null }

    # ------------------------------------------------------------ libtalloc.so (talloc.c only, like the waf build)
    Push-Location $tallocSrc
    try {
        Invoke-Tool $clang (@($target, '-O2', '-fPIC', '-shared', '-fvisibility=hidden', '-fstack-protector-strong',
            '-D_GNU_SOURCE', '-D__STDC_WANT_LIB_EXT1__=1', "-I$(Join-Path $here 'compat\talloc')", '-I.', '-Ilib/replace',
            '-Wl,-soname,libtalloc.so') + $ldShared + @('-o', (Join-Path $out 'libtalloc.so'), 'talloc.c')) | Out-Null
    } finally { Pop-Location }

    # ------------------------------------------------------------ libandroid-shmem.so (upstream Makefile flags)
    # -include fcntl.h / sys/ioctl.h and _PATH_TMP stand in for the Termux NDK sysroot patches this file relies on.
    Push-Location $shmemSrc
    try {
        Invoke-Tool $clang (@($target, '-O2', '-fpic', '-shared', '-std=c11', '-Wall', '-Wextra', '-fstack-protector-strong',
            '-include', 'fcntl.h', '-include', 'sys/ioctl.h', '-D_PATH_TMP="/data/data/com.termux/files/usr/tmp/"',
            '-Wl,--version-script=exports.txt', '-Wl,-soname,libandroid-shmem.so') + $ldShared +
            @('-o', (Join-Path $out 'libandroid-shmem.so'), 'shmem.c', '-llog', '-landroid')) | Out-Null
    } finally { Pop-Location }

    # ------------------------------------------------------------ PRoot (GNUmakefile logic, PROOT_UNBUNDLE_LOADER)
    Push-Location $prootSrc
    try {
        $archDefs = Invoke-Tool $clang @($target, '-E', '-dM', '-DNO_LIBC_HEADER', 'arch.h')
        function Get-ArchDef([string]$n) {
            $m = $archDefs | Where-Object { $_ -match "^#define $n( |$)" } | Select-Object -First 1
            if ($m) { return ($m -split ' ', 3)[2] } else { return $null }
        }
        $loaderAddress = Get-ArchDef 'LOADER_ADDRESS'
        if (-not $loaderAddress) { throw "${abi}: LOADER_ADDRESS not defined by arch.h" }
        $loaderArch = @(); $la = Get-ArchDef 'LOADER_ARCH_CFLAGS'; if ($la) { $loaderArch = $la -split ' ' }
        $cpp = @('-D_FILE_OFFSET_BITS=64', '-D_GNU_SOURCE', "-I$obj", '-I.', '-DARG_MAX=131072', '-DVERSION="5.1.107.90"')
        $cflags = @('-Wall', '-Wextra', '-O2')

        # loader (static, freestanding, at LOADER_ADDRESS). termux-packages installs it as libexec/proot/loader;
        # DSHBox ships it as libproot-loader.so. No loader32: DSHBox ships none (32-bit guests unsupported, as before).
        Invoke-Tool $clang (@($target) + $cpp + $cflags + @('-fPIC', '-ffreestanding') + $loaderArch + @('-c', 'loader/loader.c', '-o', "$obj/loader.o")) | Out-Null
        Invoke-Tool $clang (@($target) + $cpp + $cflags + @('-fPIC', '-ffreestanding') + $loaderArch + @('-c', 'loader/assembly.S', '-o', "$obj/assembly.o")) | Out-Null
        $loaderFull = "$obj/loader"   # unstripped: loader-info.c needs its symbols
        $loader = Join-Path $out 'libproot-loader.so'
        Invoke-Tool $clang @($target, '-o', $loaderFull, "$obj/loader.o", "$obj/assembly.o", '-static', '-nostdlib',
            "-Wl,--build-id=none,-Ttext=$loaderAddress,--rosegment,-z,noexecstack", '-Wl,-z,max-page-size=16384') | Out-Null
        Invoke-Tool $strip @('--strip-all', '-o', $loader, $loaderFull) | Out-Null

        # build.h: same feature probes as the GNUmakefile (compile + link), VERSION comes from -DVERSION.
        $buildH = @('/* This file is auto-generated, edit at your own risk.  */', '#ifndef BUILD_H', '#define BUILD_H')
        foreach ($f in 'process_vm', 'seccomp_filter') {
            & $clang $target @cpp @cflags -o "$obj/.check_$f" ".check_$f.c" *> $null
            if ($LASTEXITCODE -eq 0) { $buildH += "#define HAVE_$($f.ToUpperInvariant())" }
        }
        $buildH += '#endif /* BUILD_H */'
        Set-Content -Path "$obj/build.h" -Value $buildH -Encoding ascii
        if (-not ($buildH -contains '#define HAVE_PROCESS_VM') -or -not ($buildH -contains '#define HAVE_SECCOMP_FILTER')) {
            throw "${abi}: feature probes failed (build.h: $($buildH -join ' / ')); the DSHBox binary has both"
        }

        $make = Get-Content 'GNUmakefile' -Raw
        $objects = [regex]::Matches($make, '(?m)^\s*([\w/.-]+\.o)\s*\\?\s*$') |
            ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -notmatch '^loader/' -and $_ -notmatch '^\.check' }
        if ($objects.Count -lt 60) { throw "${abi}: only $($objects.Count) objects parsed from GNUmakefile" }
        $objects = @($objects)
        if (Get-ArchDef 'HAS_POKEDATA_WORKAROUND') {
            # = readelf -s loader | awk -f loader/loader-info.awk
            $syms = Invoke-Tool $readelf @('-sW', $loaderFull)
            $addr = @{}
            foreach ($s in $syms) {
                $c = $s.Trim() -split '\s+'
                if ($c.Count -ge 8 -and ($c[7] -eq 'pokedata_workaround' -or $c[7] -eq '_start')) { $addr[$c[7]] = [Convert]::ToInt64($c[1], 16) }
            }
            if ($addr.Count -ne 2) { throw "${abi}: pokedata_workaround / _start not found in the loader" }
            Set-Content -Path "$obj/loader-info.c" -Encoding ascii -Value @('#include <unistd.h>',
                "const ssize_t offset_to_pokedata_workaround=$($addr['pokedata_workaround'] - $addr['_start']);")
            $objects += 'loader/loader-info.o'
        }

        $prootCflags = $cflags + @('-fPIE', '-fstack-protector-strong', '-DWITH_LIBANDROID_SHMEM',
            '-DPROOT_UNBUNDLE_LOADER="/data/data/com.termux/files/usr/libexec/proot"',
            '-include', (Join-Path $here 'compat\termux-parity.h'),
            "-I$(Join-Path $obj 'shm-include')", "-I$tallocSrc")
        # termux-packages installs libandroid-shmem's shm.h as <sys/shm.h>; PRoot's sysvipc extension includes that.
        New-Item -ItemType Directory -Force (Join-Path $obj 'shm-include\sys') | Out-Null
        Copy-Item (Join-Path $shmemSrc 'shm.h') (Join-Path $obj 'shm-include\sys\shm.h')
        $objFiles = @()
        foreach ($o in $objects) {
            $c = $o -replace '\.o$', '.c'
            $file = if ($o -eq 'loader/loader-info.o') { "$obj/loader-info.c" } else { $c }
            $extra = @()
            # NDK r27 headers do not pull <string.h> in transitively the way the Termux sysroot does.
            if ($o -like 'extension/ashmem_memfd/*') { $extra = @('-include', 'string.h') }
            $dst = Join-Path $obj ($o -replace '/', '_')
            Invoke-Tool $clang (@($target) + $cpp + $prootCflags + $extra + @('-c', $file, '-o', $dst)) | Out-Null
            $objFiles += $dst
        }
        Invoke-Tool $clang (@($target, '-pie', '-o', (Join-Path $out 'libproot.so')) + $objFiles +
            @("-L$out", '-ltalloc', '-landroid-shmem', '-Wl,-rpath,$ORIGIN') + $ldShared) | Out-Null
    } finally { Pop-Location }

    # ------------------------------------------------------------ checks (drop-in for the DSHBox jniLibs)
    function Get-Dyn([string]$f) { return (Invoke-Tool $readelf @('-dW', $f)) }
    function Get-Needed([string]$f) { return @(Get-Dyn $f | Where-Object { $_ -match '\(NEEDED\)' } | ForEach-Object { ($_ -split '\[|\]')[1] }) }
    function Get-Defined([string]$f) { return @(Invoke-Tool $nm @('-D', '--defined-only', $f) | ForEach-Object { ($_.Trim() -split '\s+')[-1] }) }
    $expectNeeded = @{
        'libproot.so' = 'libtalloc.so', 'libandroid-shmem.so', 'libc.so'
        'libtalloc.so' = @('libc.so')
        'libandroid-shmem.so' = 'liblog.so', 'libc.so'
    }
    foreach ($f in 'libproot.so', 'libproot-loader.so', 'libtalloc.so', 'libandroid-shmem.so') {
        $p = Join-Path $out $f
        foreach ($s in (Invoke-Tool $readelf @('-lW', $p) | Where-Object { $_ -match '^\s+LOAD\s' })) {
            $align = [Convert]::ToInt64(($s.Trim() -split '\s+')[-1], 16)
            if ($align -lt 0x4000) { throw "${abi}/${f}: LOAD segment aligned below 16 KB: $s" }
        }
        $bytes = [System.IO.File]::ReadAllBytes($p)
        $text = [System.Text.Encoding]::Latin1.GetString($bytes)
        foreach ($leak in @($repo, $repo.Replace('\', '/'), $work, $work.Replace('\', '/'))) {
            if ($text.Contains($leak)) { throw "${abi}/${f}: contains the build path $leak (not reproducible)" }
        }
        if ($expectNeeded.ContainsKey($f)) {
            $needed = Get-Needed $p
            if (($needed -join ',') -ne ($expectNeeded[$f] -join ',')) { throw "${abi}/${f}: NEEDED [$($needed -join ', ')] != [$($expectNeeded[$f] -join ', ')]" }
        }
        $sums.Add("$((Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant())  $abi/$f")
    }
    $prootDyn = (Get-Dyn (Join-Path $out 'libproot.so')) -join "`n"
    if ($prootDyn -notmatch 'RUNPATH\)\s+Library runpath: \[\$ORIGIN\]') { throw "${abi}/libproot.so: RUNPATH is not `$ORIGIN" }
    if ($prootDyn -notmatch 'FLAGS_1\)\s+.*PIE') { throw "${abi}/libproot.so: not a PIE executable" }
    $loaderHdr = (Invoke-Tool $readelf @('-hW', $loader)) -join "`n"
    if ($loaderHdr -notmatch "Entry point address:\s+$([regex]::Escape($loaderAddress))\b") { throw "${abi}: loader entry != $loaderAddress" }
    if ((Invoke-Tool $readelf @('-dW', $loader)) -match 'NEEDED') { throw "${abi}: loader is not static" }
    $shmemExports = (Get-Defined (Join-Path $out 'libandroid-shmem.so') | Sort-Object) -join ','
    $shmemWant = 'libandroid_shmat,libandroid_shmat_fd,libandroid_shmctl,libandroid_shmdt,libandroid_shmdt_fd,libandroid_shmget,shmat,shmctl,shmdt,shmget'
    if ($shmemExports -ne $shmemWant) { throw "${abi}/libandroid-shmem.so: exports [$shmemExports] != [$shmemWant]" }
    $tallocDefs = [System.Collections.Generic.HashSet[string]]::new([string[]](Get-Defined (Join-Path $out 'libtalloc.so')))
    if ($tallocDefs.Contains('talloc_lib_init')) { throw "${abi}/libtalloc.so: exports talloc_lib_init (internal constructor)" }
    $shmemDefs = [System.Collections.Generic.HashSet[string]]::new([string[]]$shmemWant.Split(','))
    $missing = @(Invoke-Tool $nm @('-D', '--undefined-only', (Join-Path $out 'libproot.so')) | ForEach-Object { ($_.Trim() -split '\s+')[-1] } |
        Where-Object { $_ -notmatch '@' -and $_ -notmatch '^_binary_licenses_' -and -not $tallocDefs.Contains($_) -and -not $shmemDefs.Contains($_) })
    if ($missing.Count) { throw "${abi}/libproot.so: unresolved non-libc imports: $($missing -join ', ')" }
    $prootText = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes((Join-Path $out 'libproot.so')))
    foreach ($want in '5.1.107.90', '/data/data/com.termux/files/usr/libexec/proot/loader', '/data/data/com.termux/files/usr/tmp/') {
        if (-not $prootText.Contains($want)) { throw "${abi}/libproot.so: missing string '$want'" }
    }
    Write-Host "proot [$abi] loader @ $loaderAddress, NEEDED / exports / 16 KB alignment / RUNPATH ok"
}
[System.IO.File]::WriteAllText((Join-Path $outRoot 'SHA256SUMS'), (($sums -join "`n") + "`n"))   # LF, = build.sh
$sums | ForEach-Object { Write-Host $_ }
