/* No process/network facility is available to the genuine browser Perl. A
 * caught Perl/XS error still revokes the operation through the C++ latch. */
#include <errno.h>
#include <stdio.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>
extern void oc_biber_latch_forbidden(void);
static int denied(void) { oc_biber_latch_forbidden(); errno = EPERM; return -1; }
int __wrap_system(const char *s) { (void)s; return denied(); }
FILE *__wrap_popen(const char *s, const char *m) { (void)s; (void)m; denied(); return NULL; }
pid_t __wrap_fork(void) { return denied(); }
pid_t __wrap_vfork(void) { return denied(); }
int __wrap_execve(const char *p, char *const a[], char *const e[]) { (void)p; (void)a; (void)e; return denied(); }
int __wrap_execv(const char *p, char *const a[]) { (void)p; (void)a; return denied(); }
int __wrap_execvp(const char *p, char *const a[]) { (void)p; (void)a; return denied(); }
int __wrap_socket(int d, int t, int p) { (void)d; (void)t; (void)p; return denied(); }
int __wrap_socketpair(int d, int t, int p, int f[2]) { (void)d; (void)t; (void)p; (void)f; return denied(); }
int __wrap_connect(int f, const struct sockaddr *a, socklen_t n) { (void)f; (void)a; (void)n; return denied(); }
int __wrap_bind(int f, const struct sockaddr *a, socklen_t n) { (void)f; (void)a; (void)n; return denied(); }
int __wrap_listen(int f, int n) { (void)f; (void)n; return denied(); }
int __wrap_accept(int f, struct sockaddr *a, socklen_t *n) { (void)f; (void)a; (void)n; return denied(); }
/* Perl's Emscripten signal shim cannot suspend a browser worker. */
int sigsuspend(const void *set) { (void)set; errno = ENOSYS; return -1; }
