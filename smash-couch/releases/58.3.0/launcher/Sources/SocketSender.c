#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#include <fcntl.h>
#include <string.h>
#include <errno.h>
int couch_socket(void) {
    int fd=socket(AF_UNIX,SOCK_DGRAM,0);
    if(fd>=0){fcntl(fd,F_SETFL,O_NONBLOCK);fcntl(fd,F_SETFD,FD_CLOEXEC);}
    return fd;
}
int couch_send(int fd,const char *path,const void *data,int count) {
    struct sockaddr_un address={0};address.sun_family=AF_UNIX;
    if(strlen(path)>=sizeof(address.sun_path))return -ENAMETOOLONG;
    strcpy(address.sun_path,path);address.sun_len=sizeof(address);
    int n=(int)sendto(fd,data,(size_t)count,0,(struct sockaddr*)&address,sizeof(address));
    return n<0 ? -errno:n;
}
