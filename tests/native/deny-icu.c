/* Linux test-only loader audit: block ICU without changing installed packages.
   Apply LD_AUDIT only to the individual test process. */
#define _GNU_SOURCE
#include <link.h>
#include <stdio.h>
#include <string.h>

unsigned int la_version(unsigned int version) {
    (void)version;
    return LAV_CURRENT;
    }

char *la_objsearch(const char *name, uintptr_t *cookie, unsigned int flag) {
    (void)cookie;
    (void)flag;
    const char *basename = strrchr(name, '/');
    basename = basename == NULL ? name : basename + 1;
    if (strncmp(basename, "libicu", 6) == 0) {
        fprintf(stderr, "MNX test: ICU loading blocked: %s\n", name);
        return NULL;
        }
    return (char *)name;
    }
