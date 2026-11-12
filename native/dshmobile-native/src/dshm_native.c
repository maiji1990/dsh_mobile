// SPDX-License-Identifier: GPL-3.0-or-later
// libdshmobile_native — the minimal native boundary of DSH Mobile (ADR-0007, AGENTS.md rule 2).
//
// Only what managed code cannot do safely: fork + exec a child on a new PTY (a .NET process must not run managed code
// between fork and exec), PTY window size, waitpid, kill of a process group, and raw read/write on the PTY master.
// Written for this repo; the PTY child setup follows the usual Linux recipe (posix_openpt → setsid → TIOCSCTTY →
// dup2 → close inherited fds → reset signals → execve) that Termux's createSubprocess also uses, but no code is copied.
// Behavioural reference (GPL-3.0): DSHBox v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4,
// terminal-emulator/src/main/jni/termux.c create_subprocess (same termios flags and setup order); registered in
// docs/UPSTREAMS.md with the differences.
//
// ABI rules: every export is `dshm_*`, returns >= 0 on success and -errno on failure (spawn reports errno through an
// out-parameter), and DSHM_ABI_VERSION changes with any signature change. Managed side: src/DshMobile.Native.

#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

#define DSHM_ABI_VERSION 1
#define DSHM_EXPORT __attribute__((visibility("default")))

DSHM_EXPORT int dshm_abi_version(void) { return DSHM_ABI_VERSION; }

/* Closes every fd >= lowfd except keep. Async-signal-safe (only syscalls); maxfd is computed before fork. */
static void close_from(int lowfd, int keep, long maxfd) {
#ifdef __NR_close_range
    int ok;
    if (keep < lowfd) {
        ok = syscall(__NR_close_range, (unsigned)lowfd, ~0U, 0) == 0;
    } else {
        ok = (keep == lowfd || syscall(__NR_close_range, (unsigned)lowfd, (unsigned)(keep - 1), 0) == 0)
            && syscall(__NR_close_range, (unsigned)(keep + 1), ~0U, 0) == 0;
    }
    if (ok) return;
#endif
    for (long fd = lowfd; fd < maxfd; fd++) {
        if (fd != keep) close((int)fd);
    }
}

static void child_fail(int errfd) {
    int e = errno;
    ssize_t ignored = write(errfd, &e, sizeof e);
    (void)ignored;
    _exit(127);
}

/*
 * Starts `path` (argv / envp NULL-terminated, envp is the complete environment) as a session leader whose controlling
 * terminal is a new PTY. Returns the master fd (O_CLOEXEC) and writes the child pid, or returns -1 with *out_errno set
 * (exec failures included: the child reports errno over a CLOEXEC pipe, so a failed exec never looks like a start).
 */
DSHM_EXPORT int dshm_pty_spawn(const char *path, char *const argv[], char *const envp[], const char *cwd,
                               int rows, int cols, int *out_pid, int *out_errno) {
    *out_pid = -1;
    *out_errno = 0;
    if (path == NULL || argv == NULL || argv[0] == NULL || envp == NULL || rows <= 0 || cols <= 0) {
        *out_errno = EINVAL;
        return -1;
    }
    int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (master < 0) {
        *out_errno = errno;
        return -1;
    }
    char slave_name[64];
    if (grantpt(master) != 0 || unlockpt(master) != 0 || ptsname_r(master, slave_name, sizeof slave_name) != 0) {
        *out_errno = errno;
        close(master);
        return -1;
    }
    struct termios tios;
    if (tcgetattr(master, &tios) == 0) {
        tios.c_iflag |= IUTF8;
        tios.c_iflag &= ~(IXON | IXOFF); /* Ctrl-S must reach the shell, not freeze output */
        tcsetattr(master, TCSANOW, &tios);
    }
    struct winsize ws = {.ws_row = (unsigned short)rows, .ws_col = (unsigned short)cols};
    ioctl(master, TIOCSWINSZ, &ws);

    int errpipe[2];
    if (pipe2(errpipe, O_CLOEXEC) != 0) {
        *out_errno = errno;
        close(master);
        return -1;
    }
    long maxfd = sysconf(_SC_OPEN_MAX);
    if (maxfd <= 0 || maxfd > 65536) maxfd = 65536;

    /* No runtime signal handler may run in the child between fork and exec. */
    sigset_t all, old;
    sigfillset(&all);
    pthread_sigmask(SIG_SETMASK, &all, &old);
    pid_t pid = fork();
    if (pid == 0) {
        close(errpipe[0]);
        close(master);
        struct sigaction dfl;
        memset(&dfl, 0, sizeof dfl);
        dfl.sa_handler = SIG_DFL;
        for (int s = 1; s < NSIG; s++) sigaction(s, &dfl, NULL); /* SIGKILL/SIGSTOP fail harmlessly */
        sigset_t none;
        sigemptyset(&none);
        sigprocmask(SIG_SETMASK, &none, NULL);
        if (setsid() < 0) child_fail(errpipe[1]);
        int slave = open(slave_name, O_RDWR);
        if (slave < 0) child_fail(errpipe[1]);
        if (ioctl(slave, TIOCSCTTY, 0) < 0) child_fail(errpipe[1]);
        if (dup2(slave, 0) < 0 || dup2(slave, 1) < 0 || dup2(slave, 2) < 0) child_fail(errpipe[1]);
        if (slave > 2) close(slave);
        close_from(3, errpipe[1], maxfd);
        if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0) child_fail(errpipe[1]);
        execve(path, argv, envp);
        child_fail(errpipe[1]);
    }
    int fork_errno = errno;
    pthread_sigmask(SIG_SETMASK, &old, NULL);
    close(errpipe[1]);
    if (pid < 0) {
        close(errpipe[0]);
        close(master);
        *out_errno = fork_errno;
        return -1;
    }
    int child_errno = 0;
    ssize_t n;
    do {
        n = read(errpipe[0], &child_errno, sizeof child_errno);
    } while (n < 0 && errno == EINTR);
    close(errpipe[0]);
    if (n == (ssize_t)sizeof child_errno) {
        while (waitpid(pid, NULL, 0) < 0 && errno == EINTR) {
        }
        close(master);
        *out_errno = child_errno != 0 ? child_errno : ECHILD;
        return -1;
    }
    *out_pid = pid;
    return master;
}

DSHM_EXPORT int dshm_pty_resize(int fd, int rows, int cols) {
    if (rows <= 0 || cols <= 0) return -EINVAL;
    struct winsize ws = {.ws_row = (unsigned short)rows, .ws_col = (unsigned short)cols};
    return ioctl(fd, TIOCSWINSZ, &ws) == 0 ? 0 : -errno;
}

/*
 * Reads up to len bytes, waiting at most timeout_ms (< 0: forever). Returns the byte count, 0 at end of stream (EIO:
 * every slave fd is closed), -EAGAIN on timeout, or -errno.
 */
DSHM_EXPORT int dshm_read(int fd, uint8_t *buf, int len, int timeout_ms) {
    if (buf == NULL || len <= 0) return -EINVAL;
    struct pollfd p = {.fd = fd, .events = POLLIN};
    int r;
    do {
        r = poll(&p, 1, timeout_ms);
    } while (r < 0 && errno == EINTR);
    if (r < 0) return -errno;
    if (r == 0) return -EAGAIN;
    ssize_t n;
    do {
        n = read(fd, buf, (size_t)len);
    } while (n < 0 && errno == EINTR);
    if (n < 0) return errno == EIO ? 0 : -errno;
    return (int)n;
}

/* Writes all len bytes. Returns len or -errno. */
DSHM_EXPORT int dshm_write(int fd, const uint8_t *buf, int len) {
    if (buf == NULL || len < 0) return -EINVAL;
    int done = 0;
    while (done < len) {
        ssize_t n = write(fd, buf + done, (size_t)(len - done));
        if (n < 0) {
            if (errno == EINTR) continue;
            return -errno;
        }
        done += (int)n;
    }
    return done;
}

DSHM_EXPORT int dshm_close(int fd) { return close(fd) == 0 ? 0 : -errno; }

/*
 * waitpid for one child. Returns 1 when reaped (*out_kind 1 = exited with *out_value code, 2 = killed by signal
 * *out_value), 0 when still running (nohang), or -errno.
 */
DSHM_EXPORT int dshm_wait(int pid, int nohang, int *out_kind, int *out_value) {
    *out_kind = 0;
    *out_value = 0;
    int status = 0;
    pid_t r;
    do {
        r = waitpid(pid, &status, nohang ? WNOHANG : 0);
    } while (r < 0 && errno == EINTR);
    if (r < 0) return -errno;
    if (r == 0) return 0;
    if (WIFEXITED(status)) {
        *out_kind = 1;
        *out_value = WEXITSTATUS(status);
    } else if (WIFSIGNALED(status)) {
        *out_kind = 2;
        *out_value = WTERMSIG(status);
    }
    return 1;
}

/* kill(pid, sig), or the whole process group when group != 0. Returns 0 or -errno. */
DSHM_EXPORT int dshm_kill(int pid, int sig, int group) {
    if (pid <= 0) return -EINVAL;
    return kill(group ? -pid : pid, sig) == 0 ? 0 : -errno;
}
