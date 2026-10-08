"""Verify current-token identity, owner payment recovery and demo config."""

from support import check, compose, login, register_and_login, request
import argparse
from uuid import uuid4

def main():
    parser=argparse.ArgumentParser();parser.add_argument("--development",action="store_true");args=parser.parse_args()
    request("GET","/auth/me",expected=401)
    config=request("GET","/ui/config");check(config=={"paymentSimulationEnabled":args.development},"UI config leaks fields or wrong environment")
    marker=uuid4().hex;password="UiSupport!123456";email=f"ui-support-{marker}@example.invalid"
    owner=register_and_login(email,password);other=register_and_login(f"ui-other-{marker}@example.invalid",password)
    me=request("GET","/auth/me",token=owner);check(me['userId'] and me['permissions']==[],"Unexpected identity permissions")
    compose("exec","-T","ecommerce","dotnet","Ecommerce.Api.dll","--grant-product-admin",email)
    check(request("GET","/auth/me",token=owner)['permissions']==[],"Old token changed claims")
    owner=login(email,password);check(request("GET","/auth/me",token=owner)['permissions']==['products:manage'],"Fresh token missing permission")
    product=request("POST","/admin/products",token=owner,body={"name":"Payment recovery","description":"fixture","category":"Verification","priceCents":1000,"available":2},expected=201)
    order=request("POST","/orders",token=owner,key=marker,body={"items":[{"productId":product['id'],"quantity":1}]},expected=201)
    path=f"/orders/{order['id']}/payments"
    request("GET",path,expected=401);request("GET",path,token=other,expected=404)
    check(request("GET",path,token=owner)['payments']==[],"New order has payment attempt")
    payment=request("POST",path,token=owner,key=marker,expected=201)
    found=request("GET",path,token=owner);check(found['page']==1 and found['pageSize']==20 and found['payments']==[payment],"Cannot recover saved payment")
    check(request("GET",path+"?page=2147483647&pageSize=50",token=owner)['payments']==[],"Recovery paging overflow")
    request("GET",f"/orders/{uuid4()}/payments",token=owner,expected=404)
    print("Storefront support HTTP passed: identity snapshots, config, owner payment recovery")
if __name__=="__main__":main()
