import urllib.request
import json
import os
import sys
import subprocess
import signal
import shutil
import time
import platform

def remove_temp():
    print("remove temp")
    if os.path.exists("./hyper.old"):
        shutil.rmtree("./hyper.old")
    if os.path.exists('./events.db'):
        os.unlink('./events.db')
    print("done")

def remove_temp_after():
    print("remove temp afer install")
    if os.path.exists("./publish" + system_arch + ".tar.xz"):
        os.unlink("./publish" + system_arch + ".tar.xz")
    if os.path.exists("./publish" + system_arch):
        shutil.rmtree("./publish" + system_arch)

def dlProgress(count, blockSize, totalSize):
    percent = int(count*blockSize*100/totalSize)
    sys.stdout.write("\rdownloading...%d%%" % percent)
    sys.stdout.flush()

def make_executable(path):
    mode = os.stat(path).st_mode
    mode |= (mode & 0o444) >> 2    # copy R bits to X
    os.chmod(path, mode)

def remove_inhausUDP_cronjob():
    if os.path.exists("current.cron"):
        os.unlink("current.cron")
    if os.path.exists("new.cron"):
        os.unlink("new.cron")

    with open("current.cron", "w") as cron:
        subprocess.call("crontab -l".split(" "), stdout=cron)

    with open("current.cron", "r") as org:
        with open("new.cron", "w") as new:
            for line in org:
                if "watchdog_zwave.sh" in line and not line.startswith("#"):
                    new.write("#" + line)
                else:
                    new.write(line)

    subprocess.call("crontab ./new.cron".split(" "))


    if os.path.exists("current.cron"):
        os.unlink("current.cron")
    if os.path.exists("new.cron"):
        os.unlink("new.cron")

def check_current_directory():
    if os.getcwd() == hyper_path:
        print("Current directory is the production directory, use a working directory for update!")
        sys.exit(1)
        
#determine platform
arch = platform.machine().lower()
if arch == 'armv7l' or arch == 'armv6l':
    system_arch = 'linux-arm'
elif arch == 'aarch64' or arch == 'arm64':
    system_arch = 'linux-arm64'
else:
    system_arch = 'unknown'

print("recognized system_arch: " + system_arch)

hyper_path = "/var/inhaus/hyper"
hyper_version_path = hyper_path + "/version.txt"
hyper_version_remote_url = 'https://api.github.com/repos/inhausgmbhdu/hyper/releases/latest'
hyper_latest_url = 'https://github.com/inhausgmbhdu/hyper/releases/latest/download/publish' + system_arch + '.tar.xz'
default_com = "/dev/ttyUSB_ZStickGen5"



if len(sys.argv) > 1:
    default_com = sys.argv[1]

check_current_directory()

#get remote version
remote_version = "N/A"

#unpack if archives presend:
if os.path.exists("./publish" + system_arch):
    shutil.rmtree("./publish" + system_arch)
if os.path.exists("./publish" + system_arch + ".tar.xz"):
    print("./publish" + system_arch + ".tar.xz")
    extract_cmd = 'tar xf publish' + system_arch + '.tar.xz'
    subprocess.call(extract_cmd.split(' '))
    print("done!")
elif os.path.exists("./publish" + system_arch + ".zip"):
    print("extracting publish" + system_arch + ".zip")
    extract_cmd = 'unzip publish' + system_arch + '.zip'
    subprocess.call(extract_cmd.split(' '))
    print("done!")

if os.path.exists("./publish" + system_arch):
    with open("./publish" + system_arch + "/version.txt", 'r') as f:
        remote_version = f.read()
    print("version to install: " + remote_version)
else:
    res = urllib.request.urlopen(hyper_version_remote_url)
    res_body = res.read()
    j = json.loads(res_body.decode("utf-8"))
    remote_version = j["tag_name"]
    print("remote version: " + remote_version)

#get local version
local_version = "N/A"
if os.path.exists(hyper_version_path):
        with open(hyper_version_path, 'r') as f:
                local_version = f.read()
print("local version: " + local_version)

print("u sure? (y/n)")
sure = input()
if sure != "y":
    print("ok bye")
    sys.exit(0)

remove_temp()

if not os.path.exists("./publish" + system_arch):
    #download latest release
    print("downloading latest version")
    urllib.request.urlretrieve(hyper_latest_url, 'publish' + system_arch + '.tar.xz', reporthook=dlProgress)
    print("\ndone!")

    #extract
    print("extracting")
    extract_cmd = 'tar xf publish' + system_arch + '.tar.xz'
    subprocess.call(extract_cmd.split(' '))
    print("done!")

# remove cronjob
remove_inhausUDP_cronjob()

#stopping and removing inhHausZwave
print("removing inHausUDPzwave")
if os.path.exists("/etc/init.d/inHausUDPzwave"):
    subprocess.call("/etc/init.d/inHausUDPzwave stop".split(" "))
    os.unlink("/etc/init.d/inHausUDPzwave")
print("done")

#stop hyper
print("stopping hyper")
if os.path.exists("/etc/init.d/hyper"):
    text = open('/etc/init.d/hyper', 'r').read()
    subprocess.call("/etc/init.d/hyper stop".split(" "))
    if text.find('PRESERVE=1') == -1:
        os.unlink("/etc/init.d/hyper")
    else:
        print("preserving existing /etc/init.d/hyper")

if not os.path.exists("/etc/init.d/hyper"):
    #copy to /etc/init.d while removing windows line breaks
    text = open('./publish' + system_arch + '/hyperInitD', 'r').read().replace('\r\n', '\n')
    open("/etc/init.d/hyper", 'w').write(text)
    make_executable("/etc/init.d/hyper")
    subprocess.call("update-rc.d hyper defaults".split(" "))
print("done")


print("update udev")
if os.path.exists("/etc/udev/rules.d/20_ZStickGen5.rules"):
    os.unlink("/etc/udev/rules.d/20_ZStickGen5.rules")
if os.path.exists("/etc/udev/rules.d/20_ZwaveUSBStick.rules"):
    os.unlink("/etc/udev/rules.d/20_ZwaveUSBStick.rules")
shutil.copyfile("./publish" + system_arch + "/20_ZStickGen5.rules", "/etc/udev/rules.d/20_ZStickGen5.rules")
print("done")

#backup logs and events
print("backup")
eventsdb_file = '/var/inhaus/hyper/events.db'
if os.path.exists('/var/inhaus/hyper/logs'):
    shutil.move('/var/inhaus/hyper/logs', "./publish" + system_arch + "/")
if os.path.exists(eventsdb_file):
    if os.path.getsize(eventsdb_file) < 100000000:
        print('preserving events.db')
        shutil.move(eventsdb_file, './publish' + system_arch + '/')
    else:
        print('truncating events.db')
if os.path.exists('/var/inhaus/hyper/programconfig.yaml'):
    shutil.copyfile('/var/inhaus/hyper/programconfig.yaml', './publish' + system_arch + '/programconfig.yaml')

print("done")

#delete hyper folder
print("move old hyper to hyper.old")
if os.path.exists('/var/inhaus/hyper'):
    shutil.move('/var/inhaus/hyper', './hyper.old')
print("done")

#copy downloaded hyper folder and backups
print("move new")
shutil.move('./publish' + system_arch, '/var/inhaus/hyper')
print("done")

remove_temp_after()

#make executable
make_executable('/var/inhaus/hyper/hyper')
make_executable('/var/inhaus/hyper/ClientTCP')

if not os.path.exists('/dev/ttyUSB_ZStickGen5'):
    print("No stick detected, could be using RazBerry hat. starting hyper in background")
    subprocess.call("service hyper start".split(" "))

print("reload udev")
subprocess.call("udevadm control --reload-rules".split(" "))
subprocess.call("udevadm trigger".split(" "))
#subprocess.Popen(['nohup', './hyper', default_com], stdout=open('/dev/null', 'w'), stderr=open('logfile.log', 'a'), preexec_fn=os.setpgrp, cwd="/var/inhaus/hyper")
time.sleep(5)
print("done")

print("starting client to verify")
subprocess.call("./ClientTCP", cwd="/var/inhaus/hyper")
